import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

import provision_synthetic_stage as stage

RUNTIME = "mcr.microsoft.com/dotnet/sdk@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e"
POSTGRES = "postgres@sha256:bd5dfa494a5a259704365813dbfa5fa6b3e69be7348d43b547f540c730478583"


def check(executable, schema, workspace):
    schema_hash = hashlib.sha256(schema.read_bytes()).hexdigest()
    print(f"Synthetic canary schema SHA-256 {schema_hash}", flush=True)
    stage.ROOT = workspace / "fixture"
    stage.SDK = stage.output("docker", "image", "inspect", "--format", "{{.Id}}", RUNTIME)
    db_image = stage.output("docker", "image", "inspect", "--format", "{{.Id}}", POSTGRES)
    assert db_image == stage.output("docker", "image", "inspect", "--format", "{{.Id}}", "postgres:18.6")
    bundle = workspace / "bundle"
    bundle.mkdir()
    (bundle / "Platform.Api").write_bytes(executable.read_bytes())
    (bundle / "Platform.Api").chmod(0o755)
    settings = stage.provision(bundle, schema)
    assert settings[2] == schema_hash
    compose_file = stage.ROOT / "compose.json"
    config = json.loads(compose_file.read_text())
    db, bot = (config["services"][n] for n in ("stage-db", "stage-bot-a"))
    config["services"] = {"stage-db": db, "stage-bot-a": bot}
    for service, size in ((db, "128m"), (bot, "64m")):
        service.update(cpus=0.1, mem_limit=size, memswap_limit=size)
    bot["entrypoint"] = ["/opt/runtime/Platform.Api", "--serve-synthetic-staging", "bot-a"]
    compose_file.chmod(0o600)
    compose_file.write_text(json.dumps(config))
    compose_file.chmod(0o400)
    inspector = "shop-staging-ci-inspector"
    try:
        stage.compose("up", "-d", "--no-deps", "--pull", "never", "stage-db")
        wait_for(lambda: subprocess.run(
            ["docker", "compose", "-f", str(compose_file), "exec", "-T", "stage-db", "pg_isready", "-q", "-h", "127.0.0.1", "-d", stage.DB],
            capture_output=True, timeout=6).returncode == 0)
        stage.initialize(*settings)
        stage.run("docker", "run", "-d", "--name", inspector, "--pull", "never", "--network", "shop-staging-private",
                  "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--cpus", ".1",
                  "--memory", "48m", "--memory-swap", "48m", "--pids-limit", "20",
                  "--mount", f"type=bind,src={stage.ROOT}/operator/stage-bot-a.crt,dst=/cert,readonly",
                  "--entrypoint", "sleep", stage.SDK, "180", stdout=subprocess.DEVNULL)
        header = (stage.ROOT / "operator/bot-a-header").read_text()
        payload = {"update_id": 501, "callback_query": {"from": {"id": 7001, "is_bot": False},
                   "data": "v1|1|p|" + stage.PRODUCT.replace("-", ""), "message": {"from": {"id": 1001, "is_bot": True},
                   "message_id": 77, "chat": {"type": "private", "id": 7001}}}}
        stage.compose("up", "-d", "--no-deps", "--pull", "never", "stage-bot-a")
        wait_for(lambda: request(inspector, "/ready") == "bot-a\n200")
        wrong = dict(payload, callback_query=dict(payload["callback_query"], message=dict(payload["callback_query"]["message"], **{"from": {"id": 1002, "is_bot": True}})))
        assert request(inspector, "/callbacks/stage-a", header, wrong).endswith("\n401")
        assert request(inspector, "/callbacks/stage-a", header, payload) == "Product\n200"
        stage.compose("stop", "-t", "10", "stage-bot-a")
        assert stage.query("SELECT count(*) FROM shop.orders;") == "0"
        stage.compose("up", "-d", "--no-deps", "--pull", "never", "stage-bot-a")
        wait_for(lambda: request(inspector, "/ready") == "bot-a\n200")
        assert request(inspector, "/callbacks/stage-a", header, payload) == "Duplicate\n200"
        assert stage.query("SELECT count(*) FROM shop.bot_activity_events WHERE telegram_update_id=501;") == "1"
        assert stage.query("SELECT count(*) FROM shop.orders;") == "0"
        print("PASS native staging catalog, wrong-bot rejection, independent stop and restart replay against the matching synthetic schema")
    except (AssertionError, RuntimeError, subprocess.SubprocessError):
        stage.compose("ps", "--all")
        stage.compose("logs", "--tail", "12", "stage-bot-a")
        raise
    finally:
        subprocess.run(["docker", "rm", "-f", inspector], capture_output=True, timeout=15)
        stage.compose("down", "--volumes", "--timeout", "10")


def request(inspector, route, key=None, payload=None):
    data = ""
    if key is not None:
        data = "header = " + json.dumps("X-Telegram-Bot-Api-Secret-Token: " + key) + "\n"
        data += 'header = "Content-Type: application/json"\ndata-binary = ' + json.dumps(json.dumps(payload)) + "\n"
    result = subprocess.run(["docker", "exec", "-i", inspector, "curl", "--silent", "--show-error", "--max-time", "5",
                             "--noproxy", "*", "--cacert", "/cert", "--config", "-", "--write-out", "\n%{http_code}",
                             "https://stage-bot-a:8443" + route], input=data, capture_output=True, text=True, timeout=8)
    return result.stdout if result.returncode == 0 else ""


def wait_for(predicate):
    for _ in range(35):
        if predicate():
            return
        time.sleep(1)
    raise RuntimeError("Disposable staging actor did not become ready")


if __name__ == "__main__":
    os.umask(0o077)
    with tempfile.TemporaryDirectory(prefix="t081-ci-") as directory:
        check(Path(sys.argv[1]).resolve(strict=True), Path(sys.argv[2]).resolve(strict=True), Path(directory))
