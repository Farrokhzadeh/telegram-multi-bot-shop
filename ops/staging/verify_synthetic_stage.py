import json
from pathlib import Path
import subprocess
import time
from provision_synthetic_stage import BRANDS, PRODUCT, ROOT, ROLES, SDK, compose, output, query, run

def validate_expected_mounts(root, role, service):
    expected = {"SHOP_STAGING_MODE", "SHOP_STAGING_DEPLOYMENT", "SHOP_STAGING_SCHEMA_SHA256",
                "DOTNET_PROCESSOR_COUNT", "DOTNET_EnableDiagnostics"}
    assert set(service["environment"]) == expected
    mount = [entry for entry in service["volumes"] if ":/run/staging:ro" in entry]
    assert mount == [f"{root}/roles/{role}:/run/staging:ro"]
    assert set(path.name for path in (root / "roles" / role).iterdir()) == (
        {"db-password", "tls.pfx"} | ({"webhook"} if role.startswith("bot-") else
                                     {"recipient-probe"} if role == "jobs" else set()))


def inspect(kind, name):
    return json.loads(output("docker", kind, "inspect", name))[0]

def verify():
    stage = json.loads((ROOT / "operator/identity.json").read_text())
    assert stage["environment"] == "isolated-synthetic-v2"
    network = inspect("network", "shop-staging-private")
    assert network["Internal"] and not network["EnableIPv6"]
    assert network["Options"]["com.docker.network.bridge.gateway_mode_ipv4"] == "isolated"
    assert network["Labels"]["com.docker.compose.project"] == "shop-staging"
    assert len(network["Containers"]) == 6
    for container in network["Containers"]:
        item = inspect("container", container)
        labels = item["Config"]["Labels"]
        host = item["HostConfig"]
        service = labels["com.docker.compose.service"]
        assert labels["com.docker.compose.project"] == "shop-staging"
        assert item["State"]["Running"] and not host["Privileged"] and not host["PortBindings"] and host["ReadonlyRootfs"]
        assert host["CapDrop"] == ["ALL"] and set(item["NetworkSettings"]["Networks"]) == {"shop-staging-private"}
        assert all(mount["Source"].startswith(str(ROOT) + "/") or mount.get("Name") == "shop-staging-postgres" for mount in item["Mounts"])
        assert all(mount["Destination"] != "/var/run/docker.sock" for mount in item["Mounts"])
        if service == "stage-db":
            assert item["Image"] == stage["databaseImage"] and item["Config"]["User"] == "999:999"
            assert {mount["Name"] for mount in item["Mounts"] if mount["Type"] == "volume"} == {"shop-staging-postgres"}
            continue
        role = service.removeprefix("stage-")
        uid = ROLES[role]
        assert item["Image"] == SDK and item["Config"]["User"] == f"{uid}:{uid}"
        assert not any(mount["RW"] for mount in item["Mounts"])
        assert set(mount["Destination"] for mount in item["Mounts"]) == ({"/opt/runtime", "/run/staging", "/run/receipt"} if role.startswith("bot-") else {"/opt/runtime", "/run/staging"})
        run("docker", "exec", container, "sh", "-ec", """
            test -z "$(awk '$2 == "00000000" {print}' /proc/net/route)"
            test "$(stat -c '%a %u' /run/staging/db-password)" = "400 $(id -u)"
            test ! -e /run/staging/keys
            case "$1" in
                bot-*)
                    test -r /run/receipt/receipt
                    test ! -w /run/receipt/receipt;;
                *) test ! -e /run/receipt;;
            esac
        """, "stage", role)
    assert query("SELECT obj_description('shop'::regnamespace,'pg_namespace');") == "akanti-plaintext-v2"
    assert query("SELECT shobj_description((SELECT oid FROM pg_database WHERE datname=current_database()),'pg_database');") == (
        f"shop-staging-synthetic-v2:{stage['deployment']}:{stage['schema']}")
    for alias, brand in BRANDS.items():
        role = "shop_staging_bot_" + alias
        assert query(f"SET ROLE {role}; SELECT count(*)||':'||min(code) FROM shop.brands;") == f"1:stage-{alias}"
        assert query(f"SET ROLE {role}; SELECT count(*) FROM shop.users WHERE brand_id<>'{brand}';") == "0"
        assert query(f"SELECT has_schema_privilege('{role}','shop','CREATE') OR has_table_privilege('{role}','shop.admin_sessions','SELECT') OR has_table_privilege('{role}','shop.inventory_items','SELECT');") == "f"
    assert query("SELECT count(*) FROM pg_tables WHERE schemaname='shop' AND tablename<>'__EFMigrationsHistory';") == "28"
    assert query("SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='shop' AND p.prosecdef AND p.proowner='shop_staging_migrator'::regrole;") == "2"
    assert query("SELECT has_function_privilege('shop_staging_maintenance','shop.activity_cleanup_candidates(integer)','EXECUTE') AND NOT has_function_privilege('shop_staging_api','shop.activity_cleanup_candidates(integer)','EXECUTE');") == "t"
    print("PASS container isolation, scope of mounts, database roles and row-level separation", flush=True)

def verify_routes():
    inspector = "shop-staging-t075-verifier"
    run("docker", "run", "-d", "--name", inspector, "--network", "shop-staging-private", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges", "--cpus", ".15", "--memory", "48m", "--memory-swap", "48m", "--pids-limit", "20",
        "--mount", f"type=bind,src={ROOT}/operator,dst=/operator,readonly", "--entrypoint", "sleep", SDK, "180",
        stdout=subprocess.DEVNULL)
    def request(host, path, status, *, headers=(), body=None, reply=None, verb="GET"):
        config = "".join("header = " + json.dumps(h) + "\n" for h in headers)
        if body is not None:
            config += "header = \"Content-Type: application/json\"\n"
            config += "data-binary = " + json.dumps(json.dumps(body)) + "\n"
        r = run("docker", "exec", "-i", inspector, "curl", "--silent", "--show-error", "--max-time", "8", "--noproxy", "*",
                "--cacert", "/operator/" + host + ".crt", "--request", verb, "--config", "-", "--write-out", "\n%{http_code}",
                f"https://{host}:8443{path}", input=config, text=True, stdout=subprocess.PIPE)
        response, code = r.stdout.rsplit("\n", 1)
        assert code == str(status), f"{host} {status=} actual={code}"
        if reply is not None:
            assert reply == response, f"{host} unexpected body"
    def operator(name):
        return (ROOT / "operator" / name).read_text()
    try:
        for role in ROLES:
            request("stage-" + role, "/ready", 200, reply=role)
        admin = "/v1/admin/staging"
        request("stage-api", admin, 401)
        request("stage-api", admin, 401, headers=["Authorization: Bearer " + operator("bot-a-header")])
        request("stage-api", admin, 401, headers=["Authorization: Bearer " + operator("revoked-admin")])
        request("stage-api", admin, 200, headers=["Authorization: Bearer " + operator("admin")], reply="synthetic-stage-admin")
        def callback(route, key, bot, status, reply=None, actor=7001):
            payload = {"update_id": 501, "callback_query": {"from": {"id": actor, "is_bot": False},
                "data": "v1|1|p|" + PRODUCT.replace("-", ""), "message": {"from": {"id": bot, "is_bot": True},
                "message_id": 77, "chat": {"type": "private", "id": actor}}}}
            request("stage-bot-" + route, "/callbacks/stage-" + route, status,
                    headers=["X-Telegram-Bot-Api-Secret-Token: " + operator("bot-" + key + "-header")],
                    body=payload, reply=reply, verb="POST")
        request("stage-bot-a", "/callbacks/production", 404, verb="POST")
        for i, alias in enumerate(BRANDS):
            other = "abc"[(i + 1) % 3]
            callback(alias, other, 1001 + i, 401)
            callback(alias, alias, 1001 + (i + 1) % 3, 401)
            callback(alias, alias, 1001 + i, 403, actor=7002)
            known = query(f"SELECT count(*) FROM shop.bot_activity_events e JOIN shop.users u ON u.id=e.user_id WHERE u.brand_id='{BRANDS[alias]}' AND e.telegram_update_id=501;") == "1"
            callback(alias, alias, 1001 + i, 200, "Duplicate" if known else "Product")
            callback(alias, alias, 1001 + i, 200, "Duplicate")
        assert query("SELECT count(*)||':'||count(DISTINCT u.brand_id) FROM shop.bot_activity_events e JOIN shop.users u ON u.id=e.user_id WHERE e.telegram_update_id=501;") == "3:3"
        assert query("SELECT count(*) FROM shop.orders;") == "0"
        header = "X-Staging-Probe: " + operator("recipient-probe")
        request("stage-jobs", "/fake-delivery/customer_dm/7001", 401, verb="POST")
        for kind, chat, code in [("customer_dm", 7001, 204), ("staff_chat", -10088, 204),
                                 ("customer_dm", 7002, 403), ("staff_chat", -10099, 403), ("customer_dm", -10088, 403)]:
            request("stage-jobs", f"/fake-delivery/{kind}/{chat}", code, headers=[header], verb="POST")
        print("PASS TLS, three callback routes and replay, admin authorization, test-only destinations", flush=True)
    finally:
        subprocess.run(["docker", "rm", "-f", inspector], stdout=subprocess.DEVNULL, check=True)

if __name__ == "__main__":
    verify()
    verify_routes()
