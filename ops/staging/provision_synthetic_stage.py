import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import uuid

ROOT = Path("/var/lib/teleg-cli/shop-staging")
ROLES = {"api": 10001, "bot-a": 10011, "bot-b": 10012, "bot-c": 10013, "jobs": 10021}
BRANDS = {alias: str(uuid.UUID(alias * 32)) for alias in "abc"}
SDK = "sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e"
PRODUCT = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"
DB = "shop_staging"
OWNER = "shop_staging_migrator"

def run(*args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)

def output(*args):
    return run(*args, stdout=subprocess.PIPE, text=True).stdout.strip()

def put(path, value, uid=0):
    path.write_bytes(value.encode() if isinstance(value, str) else value)
    os.chown(path, uid, uid)
    path.chmod(0o400)

def token(value):
    return base64.urlsafe_b64encode(value).decode().rstrip("=")

def provision(runtime, schema, plaintext_sha256):
    if not (os.geteuid() == 0 and runtime.is_dir() and schema.is_file()):
        raise ValueError("Staging requires a reviewed artifact and operator access")
    ddl = schema.read_bytes()
    schema_hash = hashlib.sha256(ddl).hexdigest()
    if schema_hash != plaintext_sha256:
        raise ValueError("Reviewed plaintext schema digest differs")
    sql = ddl.decode("utf-8")
    if sql.splitlines().count("COMMENT ON SCHEMA shop IS 'akanti-plaintext-v2';") != 1:
        raise ValueError("A version 2 PostgreSQL schema is required")
    mode = "isolated-synthetic-v2"
    if output("docker", "ps", "-aq", "--filter", "label=com.docker.compose.project=shop-staging"):
        raise ValueError("Staging project name is in use")
    for kind, name in [("network", "shop-staging-private"), ("volume", "shop-staging-postgres")]:
        if subprocess.run(["docker", kind, "inspect", name], capture_output=True).returncode == 0:
            raise ValueError("Staging resource name is in use")
    ROOT.mkdir(mode=0o700)
    deploy = str(uuid.uuid4())
    pg = output("docker", "image", "inspect", "postgres:18.6", "--format", "{{.Id}}")
    built = ROOT / "runtime"
    shutil.copytree(runtime, built)
    artifacts = {str(path.relative_to(built)): hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(built.rglob("*")) if path.is_file()}
    run("chmod", "-R", "a+rX", built)
    admin = secrets.token_bytes(32)
    staff_probe = secrets.token_hex(32)
    passwords = {role: secrets.token_hex(32) for role in ["root", *ROLES]}
    for part in ("roles", "assets", "backups", "operator", "bootstrap"):
        (ROOT / part).mkdir()
    put(ROOT / "bootstrap/root-password", passwords["root"], 999)
    put(ROOT / "operator/admin", token(admin))
    put(ROOT / "operator/recipient-probe", staff_probe)
    for alias in BRANDS:
        (ROOT / "assets" / alias).mkdir()
        put(ROOT / "assets" / alias / "receipt", "synthetic staging receipt", ROLES["bot-" + alias])
        os.chown(ROOT / "assets" / alias, ROLES["bot-" + alias], ROLES["bot-" + alias])
    limits = {"read_only": True, "cap_drop": ["ALL"], "security_opt": ["no-new-privileges:true"], "restart": "no", "pull_policy": "never",
              "cpus": 0.18, "mem_limit": "112m", "memswap_limit": "112m", "pids_limit": 55,
              "logging": {"driver": "json-file", "options": {"max-size": "1m", "max-file": "2"}}}
    services = {
        "stage-db": {**limits, "image": pg, "user": "999:999", "mem_limit": "160m", "memswap_limit": "160m",
                     "environment": {"POSTGRES_DB": DB, "POSTGRES_PASSWORD_FILE": "/run/bootstrap/root-password",
                                     "POSTGRES_INITDB_ARGS": "--auth-local=scram-sha-256 --auth-host=scram-sha-256"},
                     "tmpfs": ["/tmp:rw,nosuid,size=16m", "/var/run/postgresql:rw,nosuid,size=8m,uid=999,gid=999"],
                     "volumes": [f"{ROOT}/bootstrap/root-password:/run/bootstrap/root-password:ro", "postgres:/var/lib/postgresql"],
                     "command": ["-c", "shared_buffers=16MB", "-c", "max_connections=18", "-c", "work_mem=1MB"]}
    }
    fingerprints = {}
    for role, uid in ROLES.items():
        directory = ROOT / "roles" / role
        directory.mkdir()
        host = "stage-" + role
        cert = ROOT / "operator" / (host + ".crt")
        private = ROOT / "operator" / (host + ".temporary")
        run("openssl", "req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:prime256v1",
            "-nodes", "-subj", "/CN=" + host, "-addext", "subjectAltName=DNS:" + host, "-days", "30",
            "-keyout", private, "-out", cert, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        run("openssl", "pkcs12", "-export", "-in", cert, "-inkey", private, "-out", directory / "tls.pfx", "-passout", "pass:")
        private.unlink()
        fingerprints[host] = output("openssl", "x509", "-in", cert, "-noout", "-fingerprint", "-sha256")
        bot = role.startswith("bot-")
        put(directory / "db-password", passwords[role], uid)
        if bot:
            value = secrets.token_bytes(32)
            put(directory / "webhook", value, uid)
            put(ROOT / "operator" / (role + "-header"), token(value))
        if role == "jobs":
            put(directory / "recipient-probe", staff_probe, uid)
        os.chown(directory / "tls.pfx", uid, uid)
        (directory / "tls.pfx").chmod(0o400)
        os.chown(directory, uid, uid)
        mounts = [f"{built}:/opt/runtime:ro", f"{directory}:/run/staging:ro"]
        if bot:
            mounts.append(f"{ROOT}/assets/{role[-1]}:/run/receipt:ro")
        services[host] = {
            **limits, "image": SDK, "user": f"{uid}:{uid}", "init": True,
            "entrypoint": ["dotnet", "/opt/runtime/Platform.Api.dll", "--serve-synthetic-staging", role],
            "environment": {"SHOP_STAGING_MODE": mode, "SHOP_STAGING_DEPLOYMENT": deploy, "SHOP_STAGING_SCHEMA_SHA256": schema_hash,
                            "DOTNET_PROCESSOR_COUNT": "1", "DOTNET_EnableDiagnostics": "0"},
            "volumes": mounts, "tmpfs": ["/tmp:rw,nosuid,size=16m"], "depends_on": ["stage-db"]}
    config = {"name": "shop-staging", "services": services, "volumes": {"postgres": {"name": "shop-staging-postgres"}},
              "networks": {"default": {"name": "shop-staging-private", "internal": True,
                          "driver_opts": {"com.docker.network.bridge.gateway_mode_ipv4": "isolated"}}}}
    put(ROOT / "compose.json", json.dumps(config))
    put(ROOT / "operator/identity.json", json.dumps({"deployment": deploy, "environment": mode,
        "database": DB, "schema": schema_hash, "runtime": artifacts, "runtimeImage": SDK, "databaseImage": pg,
        "brands": BRANDS, "roles": ROLES, "certificates": fingerprints,
        "source": "synthetic-only", "customerTester": 7001, "staffTester": -10088}))
    return sql, deploy, schema_hash, passwords, hashlib.sha256(admin).hexdigest()

def compose(*args, **kwargs):
    return run("docker", "compose", "-f", str(ROOT / "compose.json"), *args, **kwargs)

def query(sql):
    return compose("exec", "-T", "stage-db", "sh", "-ec",
                   'export PGPASSWORD=$(cat /run/bootstrap/root-password); exec psql -X -qAt -v ON_ERROR_STOP=1 -h 127.0.0.1 -U postgres -d shop_staging',
                   input=sql, stdout=subprocess.PIPE, text=True).stdout.strip()

def initialize(sql, deploy, schema_hash, passwords, admin_hash):
    roles = {role: "shop_staging_" + role.replace("-", "_") for role in ROLES}
    statements = [
        "CREATE ROLE " + OWNER + " NOLOGIN;",
        "CREATE ROLE shop_staging_maintenance NOLOGIN;",
        "REVOKE CONNECT,TEMP ON DATABASE shop_staging FROM PUBLIC;",
        "ALTER DATABASE shop_staging OWNER TO " + OWNER + ";",
    ]
    for role, name in roles.items():
        statements += [
            f"CREATE ROLE {name} LOGIN CONNECTION LIMIT 2 PASSWORD '{passwords[role]}';",
            f"GRANT CONNECT ON DATABASE shop_staging TO {name};",
        ]
    statements += [f"SET ROLE {OWNER};", sql,
                   f"COMMENT ON DATABASE shop_staging IS 'shop-staging-synthetic-v2:{deploy}:{schema_hash}';",
                   f"INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ('{PRODUCT}','local','immediate','Synthetic stage listing');"]
    for index, (alias, brand) in enumerate(BRANDS.items(), start=1001):
        statements += [
            f"INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token,active) VALUES ('{brand}','stage-{alias}','Stage {alias}','Stage {alias}','international','en','synthetic_stage_{alias}_bot',{index},decode('01','hex'),true);",
            f"INSERT INTO shop.brand_products(brand_id,product_id,visible,price_usdt) VALUES ('{brand}','{PRODUCT}',true,10);",
            f"INSERT INTO shop.users(id,brand_id,telegram_id,language,flow_state,flow_revision,flow_anchor_message_id) VALUES (md5('staging-{alias}-tester')::uuid,'{brand}',7001,'en','catalog',1,77);",
        ]
    revoked = secrets.token_bytes(32)
    put(ROOT / "operator/revoked-admin", token(revoked))
    statements += [
        "INSERT INTO shop.admins(id,login,authentication_mode,external_subject) VALUES ('abababab-abab-abab-abab-abababababab','synthetic-stage-operator','external','staging-only');",
        f"INSERT INTO shop.admin_sessions(id,admin_id,token_hash,auth_version_at_issue,expires_at,revoked_at) VALUES (gen_random_uuid(),'abababab-abab-abab-abab-abababababab',decode('{admin_hash}','hex'),0,clock_timestamp()+interval '2 hours',NULL),(gen_random_uuid(),'abababab-abab-abab-abab-abababababab',decode('{hashlib.sha256(revoked).hexdigest()}','hex'),0,clock_timestamp()+interval '2 hours',clock_timestamp());",
        "RESET ROLE;",
        "GRANT USAGE ON SCHEMA shop TO " + ",".join(roles.values()) + ",shop_staging_maintenance;",
        "GRANT SELECT (id,disabled_at,auth_version) ON shop.admins TO shop_staging_api;",
        "GRANT SELECT (id,admin_id,token_hash,auth_version_at_issue,expires_at,revoked_at) ON shop.admin_sessions TO shop_staging_api;",
        "GRANT EXECUTE ON FUNCTION shop.activity_cleanup_candidates(integer) TO shop_staging_maintenance;",
    ]
    statements.append('GRANT SELECT ON shop."__EFMigrationsHistory" TO ' + ",".join(roles.values()) + ";")
    for table in ("brands", "users", "brand_products", "bot_activity_events", "orders", "products"):
        statements.append(f"ALTER TABLE shop.{table} ENABLE ROW LEVEL SECURITY;")
    for alias, brand in BRANDS.items():
        role = roles["bot-" + alias]
        scope = f"brand_id='{brand}'"
        member = f"user_id IN (SELECT id FROM shop.users WHERE {scope})"
        statements += [
            f"GRANT SELECT ON shop.brands,shop.users,shop.brand_products,shop.products,shop.bot_activity_events,shop.orders TO {role};",
            f"GRANT UPDATE(telegram_update_highwater,telegram_update_window) ON shop.brands TO {role};",
            f"GRANT UPDATE(id) ON shop.products TO {role};",
            f"CREATE POLICY {role}_read ON shop.products FOR SELECT TO {role} USING (true);",
            f"CREATE POLICY {role}_lock ON shop.products FOR UPDATE TO {role} USING (true) WITH CHECK (false);",
            f"GRANT UPDATE(flow_state,flow_revision,flow_order_id,flow_product_id,flow_anchor_message_id,flow_expires_at,last_seen_at,blocked_at) ON shop.users TO {role};",
            f"GRANT INSERT ON shop.bot_activity_events TO {role};",
            f"GRANT EXECUTE ON FUNCTION shop.require_true(boolean,text),shop.telegram_update_seen(bigint,bytea,bigint),shop.member_brand(uuid) TO {role};",
        ]
        for table, condition in [("brands", "id='" + brand + "'"), ("users", scope), ("brand_products", scope),
                                 ("bot_activity_events", member), ("orders", member)]:
            statements.append(f"CREATE POLICY {role} ON shop.{table} TO {role} USING ({condition});")
    query("BEGIN;\n" + "\n".join(statements) + "\nCOMMIT;")

if __name__ == "__main__":
    import time
    os.umask(0o077)
    if len(sys.argv) != 5 or sys.argv[3] != "--plaintext-sha256":
        raise SystemExit("Usage: provision_synthetic_stage.py RUNTIME_DIRECTORY SCHEMA_SQL --plaintext-sha256 SHA256")
    memory = int(next(line for line in Path("/proc/meminfo").read_text().splitlines() if line.startswith("MemAvailable:")).split()[1])
    if memory < 1048576 or shutil.disk_usage(ROOT.parent).free < 3 * 1024 ** 3:
        raise SystemExit("Staging requires 1 GiB available RAM and 3 GiB free disk")
    settings = provision(Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[4])
    compose("up", "-d", "--pull", "never", "stage-db")
    for _ in range(35):
        result = subprocess.run(["docker", "compose", "-f", str(ROOT / "compose.json"), "exec", "-T", "stage-db",
                                 "pg_isready", "-q", "-h", "127.0.0.1", "-U", "postgres", "-d", DB], capture_output=True)
        if result.returncode == 0:
            break
        time.sleep(1)
    else:
        raise SystemExit("Staging PostgreSQL did not become ready")
    initialize(*settings)
    compose("up", "-d", "--pull", "never")
