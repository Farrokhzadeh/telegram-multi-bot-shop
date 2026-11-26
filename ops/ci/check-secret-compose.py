"""Render only. No images, containers or user secret paths are accessed."""
import json
import os
from pathlib import Path
import subprocess

compose = Path(__file__).resolve().parents[1] / "compose/backend.compose.yaml"
roles = {"api", "bot-a", "bot-b", "bot-c", "jobs"}
env = {key: value for key, value in os.environ.items() if not key.startswith(("SHOP_", "COMPOSE_"))}
image = "registry.invalid/qualification/disabled@sha256:" + "0" * 64
for role in ("API", "BOT", "JOB"):
    env[f"SHOP_{role}_IMAGE"] = image
result = subprocess.run(["docker", "compose", "--env-file", "/dev/null", "-f", str(compose),
                         "--profile", "configuration-only", "config", "--format", "json"], env=env, check=True, capture_output=True, text=True)
rendered = json.loads(result.stdout)
assert set(rendered["services"]) == roles, "Unexpected rendered service set"
assert not rendered.get("secrets") and not rendered.get("configs")
for service in rendered["services"].values():
    assert service["image"] == image and service["profiles"] == ["configuration-only"]
    assert service["read_only"] and service["network_mode"] == "none"
    assert service["cap_drop"] == ["ALL"] and "no-new-privileges:true" in service["security_opt"]
    assert service["user"].split(":")[0] != "0"
    assert not any(service.get(key) for key in ("ports", "volumes", "privileged", "environment", "secrets", "configs"))
    assert service["command"] == []
print("PASS: configuration-only roles are isolated and request no content-key mounts.")
