"""Render only: never pulls images, creates containers or reads user secret paths."""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

compose = Path(__file__).resolve().parents[1] / "compose/backend.compose.yaml"
source = compose.read_text()
expected = {
    "api": {"service-config.json", "brand-a-k1", "brand-b-k1", "brand-c-k1"},
    "bot-a": {"service-config.json", "brand-a-k1"},
    "bot-b": {"service-config.json", "brand-b-k1"},
    "bot-c": {"service-config.json", "brand-c-k1"},
    "jobs": {"service-config.json", "brand-a-k1", "brand-b-k1", "brand-c-k1", "inventory-k1"},
}
with tempfile.TemporaryDirectory(prefix="shop-compose-qualification-") as temporary:
    env = {key: value for key, value in os.environ.items() if not key.startswith(("SHOP_", "COMPOSE_"))}
    for variable in set(re.findall(r"\$\{(SHOP_[A-Z_]+):\?", source)):
        if variable.endswith("_IMAGE"):
            env[variable] = "registry.invalid/qualification/disabled@sha256:" + "0" * 64
        else:
            path = Path(temporary) / variable.lower()
            path.write_bytes(os.urandom(32))
            path.chmod(0o400)
            env[variable] = str(path)
    result = subprocess.run(["docker", "compose", "--env-file", "/dev/null", "-f", str(compose),
                             "--profile", "configuration-only", "config", "--format", "json"], env=env, check=True, capture_output=True, text=True)
    rendered = json.loads(result.stdout)
    assert set(rendered["services"]) == set(expected), "Unexpected rendered service set"
    for name, targets in expected.items():
        service = rendered["services"][name]
        assert service["profiles"] == ["configuration-only"]
        assert service["read_only"] and service["network_mode"] == "none"
        assert service["cap_drop"] == ["ALL"] and "no-new-privileges:true" in service["security_opt"]
        assert service["user"].split(":")[0] != "0"
        assert not service.get("ports") and not service.get("volumes") and not service.get("privileged")
        assert service["command"] == []
        assert set(service["environment"]) == {"SHOP_SECRET_DIRECTORY", "SHOP_SECRET_MANIFEST"}
        assert {item["target"] for item in service["secrets"]} == targets
        # Compose serializes FileMode as an octal string in JSON.
        assert all(int(item["mode"], 8) == 0o400 and item["uid"] == service["user"].split(":")[0]
                   for item in service["secrets"])
        if name != "jobs":
            assert "inventory_key" not in {item["source"] for item in service["secrets"]}
    assert all(Path(secret["file"]).parent == Path(temporary) for secret in rendered["secrets"].values())
print("PASS: Compose configuration parses; bot/brand/inventory secret grants are isolated; no containers started.")
