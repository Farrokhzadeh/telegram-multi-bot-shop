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
customer_url=http://127.0.0.1:18080/qualification/customer-context
test "$(curl --silent --output /dev/null --write-out '%{http_code}' "$customer_url")" = 401
test "$(curl --fail --silent -H 'X-Qualification-Bot: a' "$customer_url")" = aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa
test "$(curl --fail --silent -H 'X-Qualification-Bot: b' "$customer_url")" = bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb
test "$(curl --silent --output /dev/null --write-out '%{http_code}' -H 'X-Qualification-Bot: a' -H 'X-Brand-Id: bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' "$customer_url")" = 400
test "$(curl --silent --output /dev/null --write-out '%{http_code}' -H 'X-Qualification-Bot: a' "$customer_url?%62randId=foreign")" = 400
test "$(curl --silent --output /dev/null --write-out '%{http_code}' -H 'X-Qualification-Bot: a' "$customer_url/foreign")" = 400
test "$(curl --silent --output /dev/null --write-out '%{http_code}' -H 'X-Qualification-Bot: a' -H 'Content-Type: application/json' --data '{"nested":[{"brand_id":"foreign"}]}' "$customer_url")" = 400
test "$(curl --fail --silent -H 'X-Qualification-Bot: a' -H 'Content-Type: application/json' --data '{"text":"brand_id is ordinary message text"}' "$customer_url")" = aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa
test "$(curl --silent --output /dev/null --write-out '%{http_code}' -H 'X-Qualification-Bot: a' -H 'Content-Type: application/json' --data '{' "$customer_url")" = 400
test "$(curl --silent --output /dev/null --write-out '%{http_code}' -H 'X-Qualification-Bot: a' --data 'text=synthetic' "$customer_url")" = 415
test "$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:18080/health/ready)" = 503
echo 'T010 native HTTP probes passed: registered A/B identities, rejected selectors, bounded JSON boundary; readiness remains false.'
curl --silent --dump-header artifacts/native/fault-headers.txt --output artifacts/native/fault-response.json http://127.0.0.1:18080/qualification/fault
python3 - <<'PY'
import json,uuid
from pathlib import Path
headers=Path('artifacts/native/fault-headers.txt').read_text()
body=Path('artifacts/native/fault-response.json').read_text()
assert '500' in headers.splitlines()[0]
assert 'public-synthetic-private-detail' not in body
response=json.loads(body)
assert response['code']=='internal_error'
identifier=str(uuid.UUID(response['requestId']))
assert any(line.lower().strip()=='x-request-id: '+identifier for line in headers.splitlines())
PY
echo 'T014 native HTTP probes passed: safe error JSON and server correlation; readiness remains false.'
test "$(curl --silent --output /dev/null --write-out '%{http_code}' -H 'X-Qualification-Bot: a' -H 'X-Admin: true' http://127.0.0.1:18080/qualification/admin-boundary)" = 401
echo 'T016 native HTTP boundary passed: bot identity and admin-looking headers grant no backend privilege.'
