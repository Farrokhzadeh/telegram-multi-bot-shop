#!/usr/bin/env bash
set -euo pipefail
output=artifacts/native
publish=()
if [[ ${1:-} == --managed ]]; then
  output=artifacts/managed
  publish=(-p:PublishAot=false --no-self-contained)
fi
export AKANTI_SMOKE_OUTPUT="$output"
mkdir -p "$output"
for host in Api BotHost JobHost; do
  dotnet publish "src/Platform.${host}/Platform.${host}.csproj" -c Release -r linux-x64 --no-restore "${publish[@]}" -o "$output/${host}"
  "$output/${host}/Platform.${host}" --qualify
  file "$output/${host}/Platform.${host}"
  sha256sum "$output/${host}/Platform.${host}"
  status=0
  "$output/${host}/Platform.${host}" || status=$?
  test "$status" -eq 78
done
$output/BotHost/Platform.BotHost --qualify-ingress
dotnet publish src/Platform.SchemaRunner/Platform.SchemaRunner.csproj -c Release -r linux-x64 --no-restore "${publish[@]}" -o $output/SchemaRunner
file $output/SchemaRunner/Platform.SchemaRunner
sha256sum $output/SchemaRunner/Platform.SchemaRunner
status=0
$output/SchemaRunner/Platform.SchemaRunner || status=$?
test "$status" -eq 78
dotnet publish tests/Platform.AotSmoke/Platform.AotSmoke.csproj -c Release -r linux-x64 --no-restore "${publish[@]}" -o $output/AotSmoke
SHOP_SCHEMA_RUNNER_PATH="$PWD/$output/SchemaRunner/Platform.SchemaRunner" "$output/AotSmoke/Platform.AotSmoke"
file $output/AotSmoke/Platform.AotSmoke
sha256sum $output/AotSmoke/Platform.AotSmoke
ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production "$output/Api/Platform.Api" --serve-qualification &
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
echo 'T010 published HTTP probes passed: registered A/B identities, rejected selectors, bounded JSON boundary; readiness remains false.'
curl --silent --dump-header $output/fault-headers.txt --output $output/fault-response.json http://127.0.0.1:18080/qualification/fault
python3 - <<'PY'
import json,os,uuid
from pathlib import Path
headers=(Path(os.environ['AKANTI_SMOKE_OUTPUT']) / 'fault-headers.txt').read_text()
body=(Path(os.environ['AKANTI_SMOKE_OUTPUT']) / 'fault-response.json').read_text()
assert '500' in headers.splitlines()[0]
assert 'public-synthetic-private-detail' not in body
response=json.loads(body)
assert response['code']=='internal_error'
identifier=str(uuid.UUID(response['requestId']))
assert any(line.lower().strip()=='x-request-id: '+identifier for line in headers.splitlines())
PY
echo 'T014 published HTTP probes passed: safe error JSON and server correlation; readiness remains false.'
test "$(curl --silent --output /dev/null --write-out '%{http_code}' -H 'X-Qualification-Bot: a' -H 'X-Admin: true' http://127.0.0.1:18080/qualification/admin-boundary)" = 401
echo 'T016 published HTTP boundary passed: bot identity and admin-looking headers grant no backend privilege.'

if [[ ${1:-} == --managed ]]; then
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18080/swagger/index.html)" = 404
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18080/openapi/admin-api.json)" = 404
  kill "$api_pid"
  wait "$api_pid" || true
  ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development "$output/Api/Platform.Api" --serve-qualification &
  api_pid=$!
  for attempt in $(seq 1 50); do
    if curl --fail --silent http://127.0.0.1:18080/health/live >/dev/null; then break; fi
    sleep 0.2
  done
  curl --fail --silent --location http://127.0.0.1:18080/swagger/ -o "$output/swagger.html"
  curl --fail --silent http://127.0.0.1:18080/swagger/index.js -o "$output/swagger.js"
  grep -q '/openapi/admin-api.json' "$output/swagger.js"
  curl --fail --silent http://127.0.0.1:18080/openapi/admin-api.json | python3 -c 'import json,sys; d=json.load(sys.stdin); assert sum(map(len,d["paths"].values()))==29 and d["components"]["securitySchemes"]["adminBearer"]["scheme"]=="bearer"'
  test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18080/qualification/admin-boundary)" = 401
fi
