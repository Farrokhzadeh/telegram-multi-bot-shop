#!/usr/bin/env python3
import json
import os
import re
import secrets
import stat
import sys
from pathlib import Path

BRAND = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
DEPLOYMENT = "dddddddd-dddd-dddd-dddd-dddddddddddd"
STAGE = Path("/var/lib/akanti-staging")
ROOT = STAGE / "sqlite-private"
OWNER = 10001

def require(predicate, message):
    if not predicate:
        raise ValueError(message)

def safe_directory(path, owner):
    if path.exists():
        mode = path.lstat().st_mode
        require(stat.S_ISDIR(mode) and not stat.S_ISLNK(mode), "Unsafe stage directory")
    else:
        path.mkdir(mode=0o700)
    os.chmod(path, 0o700)
    os.chown(path, owner, owner)

def private_file(path, data, owner):
    require(isinstance(data, bytes) and 0 < len(data) < 131072, "Unsafe stage source data")
    if path.exists():
        existing = path.lstat()
        require(stat.S_ISREG(existing.st_mode) and existing.st_nlink == 1 and not stat.S_ISLNK(existing.st_mode),
                "Unsafe existing stage secret")
        require(path.read_bytes() == data, "Stage secret cannot change in place")
        return
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(data)
        os.chown(path, owner, owner)
        os.chmod(path, 0o400 if owner == OWNER else 0o600)
    except BaseException:
        path.unlink(missing_ok=True)
        raise

def read_private(path, limit):
    stats = path.lstat()
    require(stat.S_ISREG(stats.st_mode) and stats.st_nlink == 1 and not stat.S_ISLNK(stats.st_mode) and
            stats.st_size > 0 and stats.st_size <= limit and stats.st_mode & 0o077 == 0, "Unsafe private source")
    return path.read_bytes()

def inside(path, root):
    value = path.resolve(strict=True)
    require(value == root or root in value.parents, "Stage source escaped its qualified root")
    return value

def provision_tester(preview):
    require(re.fullmatch(r"/var/lib/teleg-cli/akanti-cold-staging-[A-Za-z0-9_-]+/telegram-preview-[A-Za-z0-9_-]+/config", str(preview)),
            "Unrecognized synthetic preview path")
    safe_directory(ROOT, 0)
    safe_directory(ROOT / "tester", OWNER)
    for name in ("bot-id", "bot-token", "tester-id"):
        private_file(ROOT / "tester" / name, read_private(preview / name, 200), OWNER)
    key = ROOT / "tester" / "api-key"
    if not key.exists():
        private_file(key, secrets.token_hex(32).encode("ascii"), OWNER)
    require(re.fullmatch(rb"[a-f0-9]{64}", read_private(key, 64)) is not None, "Invalid stage API key")


def main():
    require(os.geteuid() == 0, "Staging provisioning requires root")
    if len(sys.argv) == 3 and sys.argv[1] == "--plaintext":
        provision_tester(Path(sys.argv[2]).resolve(strict=True))
        print("Synthetic SQLite API and Telegram identity files are provisioned.")
        return
    require(len(sys.argv) == 4, "Staging provisioning requires exact source paths")
    target = Path(sys.argv[1]).resolve(strict=True)
    preview = Path(sys.argv[2]).resolve(strict=True)
    runtime = Path(sys.argv[3]).resolve(strict=True)
    require(re.fullmatch(r"/var/lib/teleg-cli/akanti-cold-staging-[A-Za-z0-9_-]+/commerce-[A-Za-z0-9_-]+/target", str(target))
            and re.fullmatch(r"/var/lib/teleg-cli/akanti-cold-staging-[A-Za-z0-9_-]+/telegram-preview-[A-Za-z0-9_-]+/config", str(preview))
            and runtime.name == "runtime.env" and runtime.parent.name.startswith("commerce-") and
            target.parent == runtime.parent, "Unrecognized synthetic stage paths")
    state = json.loads(read_private(target / "state.json", 32768))
    source_database = state["database"]
    require(re.fullmatch(r"shop_qualification_[A-Za-z0-9_]+", source_database), "Unqualified PG source")
    source_string = read_private(runtime, 32768).decode("utf-8")
    matches = re.findall(r"^SHOP_TEST_POSTGRES_ADMIN=(.*)$", source_string, re.MULTILINE)
    require(len(matches) == 1, "Qualified PostgreSQL connection is missing")
    dsn = matches[0]
    require(re.search(r"(?:^|;)Database=[^;]+", dsn, re.IGNORECASE) is not None, "Invalid source connection")
    dsn = re.sub(r"(?i)(^|;)Database=[^;]+", lambda match: match.group(1) + "Database=" + source_database, dsn, count=1)
    require("\n" not in dsn and "\r" not in dsn, "Invalid source connection")
    source_path = state["secrets"]
    require(isinstance(source_path, str) and source_path.startswith("/stage/target/tmp/shop-secret-qualification-"),
            "The source is not a synthetic secret snapshot")
    old_secrets = inside(target / source_path[len("/stage/target/"):], target)
    manifest = json.loads(read_private(old_secrets / "service-config.json", 32768))
    require(manifest.get("schemaVersion") == 1 and manifest.get("revision") == 7 and
            manifest.get("deploymentId") == DEPLOYMENT and manifest.get("service") == "JobHost",
            "Unqualified synthetic keys")
    entries = [value for value in manifest["brands"] if value.get("brandId") == BRAND]
    require(len(entries) == 1 and manifest.get("inventory") is not None, "Synthetic staging key is unavailable")
    provision_tester(preview)
    private_file(ROOT / "staging-copy.env", ("SHOP_STAGING_POSTGRES_READONLY=" + dsn + "\n").encode("utf-8"), 0)
    for service, root_name in (("Api", "qualified-api-v1"), ("JobHost", "qualified-job-v1")):
        qualified = ROOT / root_name
        safe_directory(qualified, OWNER)
        branch = manifest["inventory"] if service == "JobHost" else None
        revised = {
            "schemaVersion": 1,
            "revision": 7,
            "deploymentId": DEPLOYMENT,
            "service": service,
            "brands": entries,
            "inventory": branch,
        }
        refs = entries[0]["keys"] + ([] if branch is None else branch["keys"])
        names = [item["file"] for item in refs]
        require(len(names) == len(set(names)), "Duplicate synthetic key filenames")
        for name in names:
            require(re.fullmatch(r"[a-z0-9][a-z0-9-]{0,60}", name), "Invalid synthetic key file")
            private_file(qualified / name, read_private(inside(old_secrets / name, old_secrets), 64), OWNER)
        payload = (json.dumps(revised, separators=(",", ":")) + "\n").encode("utf-8")
        private_file(qualified / "service-config.json", payload, OWNER)
    print("Qualified one-brand staging secret scope and copy source are provisioned without exposing their contents.")

if __name__ == "__main__":
    main()
