#!/usr/bin/env bash
set -euo pipefail
mkdir -p artifacts/native
for host in Api BotHost JobHost; do
  dotnet publish "src/Platform.${host}/Platform.${host}.csproj" -c Release -r linux-x64 --no-restore -o "artifacts/native/${host}"
  "artifacts/native/${host}/Platform.${host}" --qualify
  file "artifacts/native/${host}/Platform.${host}"
  sha256sum "artifacts/native/${host}/Platform.${host}"
  status=0
  "artifacts/native/${host}/Platform.${host}" || status=$?
  test "$status" -eq 78
done
dotnet publish src/Platform.SchemaRunner/Platform.SchemaRunner.csproj -c Release -r linux-x64 --no-restore -o artifacts/native/SchemaRunner
file artifacts/native/SchemaRunner/Platform.SchemaRunner
sha256sum artifacts/native/SchemaRunner/Platform.SchemaRunner
status=0
artifacts/native/SchemaRunner/Platform.SchemaRunner || status=$?
test "$status" -eq 78
dotnet publish tests/Platform.AotSmoke/Platform.AotSmoke.csproj -c Release -r linux-x64 --no-restore -o artifacts/native/AotSmoke
SHOP_SCHEMA_RUNNER_PATH="$PWD/artifacts/native/SchemaRunner/Platform.SchemaRunner" artifacts/native/AotSmoke/Platform.AotSmoke
file artifacts/native/AotSmoke/Platform.AotSmoke
sha256sum artifacts/native/AotSmoke/Platform.AotSmoke
artifacts/native/Api/Platform.Api --serve-qualification &
api_pid=$!
trap 'kill "$api_pid" 2>/dev/null || true' EXIT
for attempt in $(seq 1 50); do
  if curl --fail --silent http://127.0.0.1:18080/health/live >/dev/null; then break; fi
  sleep 0.2
done
test "$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:18080/health/ready)" = 503
test "$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:18080/qualification)" = 401
curl --fail --silent -H 'X-Qualification-Token: public-synthetic-probe' http://127.0.0.1:18080/qualification | python3 -c 'import json,sys; d=json.load(sys.stdin); assert d["mode"]=="synthetic-qualification" and d["applicationReady"] is False'
