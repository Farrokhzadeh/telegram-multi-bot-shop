#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

image=${1:?}
commit=${2:?}
qualification=${3:?}
[[ $image =~ ^docker\.io/[a-z0-9][a-z0-9_-]*/akanti@sha256:[a-f0-9]{64}$ ]]
[[ $commit =~ ^[a-f0-9]{40}$ ]]
[[ $qualification =~ ^akanti-staging-[a-f0-9]{20}$ ]]
work=/var/lib/akanti-staging
persistent=$work/sqlite
private=$work/sqlite-private
previous=$work/sqlite-active
db=akanti-staging-commerce-db
source_root=$(cat "$work/shop-root")
[[ $source_root == /var/lib/teleg-cli/akanti-cold-staging-* && $(realpath -e "$source_root") == "$source_root" ]]
commerce=$(cat "$source_root/staging-commerce.path")
preview=$(cat "$source_root/telegram-preview.path")
[[ $(realpath -e "$commerce") == "$source_root"/commerce-* ]]
[[ $(realpath -e "$preview") == "$source_root"/telegram-preview-* ]]
[[ $(docker inspect -f '{{.State.Running}} {{.HostConfig.NetworkMode}} {{index .Config.Labels "org.akanti.environment"}}' "$db") == 'true none staging-commerce' ]]
[[ $(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image") == "$commit" ]]
[[ $(docker inspect -f '{{.State.Running}}' "$qualification") == true ]]
[[ $(df -PB1 "$work" | awk 'NR==2 {print $4}') -ge 1610612736 ]]

lock=$work/sqlite.lock
exec 8>"$lock"
flock -n 8

old_api=
old_worker=
old_bot=
old_commit=
if [[ -s $previous ]]; then
    mapfile -t old < "$previous"
    [[ ${#old[@]} == 5 && ${old[0]} =~ ^akanti-sqlite-api-[a-zA-Z0-9-]+$ &&
       ${old[1]} =~ ^akanti-sqlite-worker-[a-zA-Z0-9-]+$ &&
       ${old[2]} =~ ^akanti-sqlite-bot-[a-zA-Z0-9-]+$ &&
       ${old[3]} =~ ^sha256:[a-f0-9]{64}$ && ${old[4]} =~ ^[a-f0-9]{40}$ ]]
    old_api=${old[0]}
    old_worker=${old[1]}
    old_bot=${old[2]}
    old_commit=${old[4]}
    [[ $(docker inspect -f '{{.State.Running}}' "$old_api") == true ]]
    [[ $(docker inspect -f '{{.State.Running}}' "$old_worker") == true ]]
    [[ $(docker inspect -f '{{.State.Running}}' "$old_bot") == true ]]
    [[ -f $persistent/shop.db ]]
    if [[ $old_commit == "$commit" ]]; then
        printf 'Persistent SQLite staging is already serving %s\n' "$commit"
        exit 0
    fi
else
    [[ ! -e $persistent/shop.db ]] || { printf 'Unclaimed persistent SQLite state: refusing PG fallback.\n' >&2; exit 1; }
    mapfile -t pg < "$work/shop-active"
    [[ ${#pg[@]} == 4 && ${pg[0]} =~ ^akanti-staging-[a-zA-Z0-9-]+-core$ &&
       ${pg[1]} == "${pg[0]%-core}" && ${pg[3]} =~ ^[a-f0-9]{40}$ ]]
    [[ $(docker inspect -f '{{.State.Running}} {{index .Config.Labels "org.akanti.environment"}}' "${pg[0]}") == 'true staging-shop' ]]
    systemctl is-active --quiet "${pg[1]}"
    [[ -s $commerce/target/ready.json && -s $commerce/target/telegram-ready ]]
fi

install -d -m 0700 "$persistent"
chown 10001:10001 "$persistent"
docker exec "$qualification" cat /app/sqlite-stage-provision.py > "$work/sqlite-stage-provision.py.next"
chmod 0500 "$work/sqlite-stage-provision.py.next"
python3 "$work/sqlite-stage-provision.py.next" "$commerce/target" "$preview/config" "$commerce/runtime.env"
mv -f "$work/sqlite-stage-provision.py.next" "$work/sqlite-stage-provision.py"
[[ -f $private/qualified-api-v1/service-config.json &&
   -f $private/qualified-job-v1/service-config.json &&
   -f $private/tester/api-key ]]

digest=${image##*:}
release="${digest:0:12}-${commit:0:8}"
api=akanti-sqlite-api-$release
worker=akanti-sqlite-worker-$release
bot=akanti-sqlite-bot-$release
for name in "$api" "$worker" "$bot"; do
    ! docker container inspect "$name" >/dev/null 2>&1 || {
        printf 'An existing candidate container needs explicit reconciliation.\n' >&2
        exit 1
    }
done

switched=0
succeeded=0
cleanup() {
    local result=$?
    if (( succeeded )); then return 0; fi
    for name in "$bot" "$worker" "$api"; do
        docker rm -f "$name" >/dev/null 2>&1 || true
    done
    if (( switched )); then
        if [[ -n $old_api ]]; then
            docker start "$old_api" >/dev/null 2>&1 || true
            docker start "$old_worker" >/dev/null 2>&1 || true
            docker start "$old_bot" >/dev/null 2>&1 || true
            printf 'Returned to the previously qualified SQLite executable with persistent database unchanged.\n' >&2
        else
            printf 'Initial cutover failed: existing SQLite database remains intact; PG Telegram poller was NOT restarted.\n' >&2
        fi
    fi
    exit "$result"
}
trap cleanup EXIT
trap 'exit 1' INT TERM

if [[ -z $old_api ]]; then
    switched=1
    systemctl stop "${pg[1]}"
    docker stop --time 10 "${pg[0]}" >/dev/null
    docker run --rm --pull never --name "akanti-sqlite-copy-$release"         --network "container:$db" --user 10001:10001         --read-only --cap-drop ALL --security-opt no-new-privileges         --cpus .4 --memory 320m --memory-swap 320m --pids-limit 55         --tmpfs /tmp:rw,nosuid,nodev,size=48m,uid=10001,gid=10001         --env-file "$private/staging-copy.env"         --mount "type=bind,src=$persistent,dst=/stage/sqlite"         --entrypoint /app/Api/Platform.Api         "$image" --copy-staging-postgres-to-sqlite --destination /stage/sqlite/shop.db
    [[ -s $persistent/shop.db ]]
    [[ $(stat -c '%u:%g %a' "$persistent/shop.db") == '10001:10001 600' ]]
else
    switched=1
    docker stop --time 15 "$old_bot" >/dev/null
    docker stop --time 15 "$old_worker" >/dev/null
    docker stop --time 15 "$old_api" >/dev/null
fi

mounts=(
    --mount "type=bind,src=$persistent,dst=/stage/sqlite"
    --mount "type=bind,src=$private/tester,dst=/stage/tester,readonly"
)
runtime=(
    --env INSTANCE_ID=staging
    --env SHOP_SQLITE_STAGING=synthetic-acc-abad-v1
    --env SHOP_STAGING_SQLITE_PATH=/stage/sqlite/shop.db
)
security=(
    --pull never --read-only --cap-drop ALL --security-opt no-new-privileges
    --user 10001:10001 --cpus .35
    --tmpfs /tmp:rw,nosuid,nodev,size=16m,uid=10001,gid=10001
    --log-driver json-file --log-opt max-size=1m --log-opt max-file=2
)

docker create --name "$api" --label org.akanti.environment=staging-sqlite     --network bridge --memory 256m --memory-swap 256m --pids-limit 60     "${security[@]}" "${runtime[@]}" "${mounts[@]}"     --env SHOP_STAGING_SECRET_ROOT=/stage/secrets/qualified-api-v1     --mount "type=bind,src=$private/qualified-api-v1,dst=/stage/secrets/qualified-api-v1,readonly"     --entrypoint /app/Api/Platform.Api "$image" --serve-sqlite-staging >/dev/null
docker start "$api" >/dev/null
ready=0
for attempt in {1..40}; do
    if docker exec "$api" sh -ec         'key=$(cat /stage/tester/api-key); test "$(curl -fsS --max-time 2 --noproxy "*" -H "X-Staging-Key: $key" http://127.0.0.1:18081/health/sqlite)" = "sqlite:ready"'         >/dev/null 2>&1; then
        ready=1
        break
    fi
    [[ $(docker inspect -f '{{.State.Running}}' "$api") == true ]] || break
    sleep 1
done
[[ $ready == 1 ]] || { printf 'Managed SQLite API health check failed.\n' >&2; exit 1; }

docker create --name "$worker" --label org.akanti.environment=staging-sqlite     --network "container:$api" --memory 256m --memory-swap 256m --pids-limit 55     "${security[@]}" "${runtime[@]}" "${mounts[@]}"     --env SHOP_STAGING_SECRET_ROOT=/stage/secrets/qualified-job-v1     --mount "type=bind,src=$private/qualified-job-v1,dst=/stage/secrets/qualified-job-v1,readonly"     --entrypoint /app/JobHost/Platform.JobHost "$image" --serve-sqlite-staging >/dev/null
docker start "$worker" >/dev/null
for attempt in {1..40}; do
    [[ $(docker inspect -f '{{.State.Running}}' "$worker") == true ]] || break
    docker logs "$worker" 2>&1 | grep -q 'managed SQLite staging delivery worker is running' && break
    sleep 1
done
[[ $(docker inspect -f '{{.State.Running}}' "$worker") == true ]] &&
    docker logs "$worker" 2>&1 | grep -q 'managed SQLite staging delivery worker is running' ||
    { printf 'Managed SQLite delivery worker health check failed.\n' >&2; exit 1; }

docker create --name "$bot" --label org.akanti.environment=staging-sqlite     --network "container:$api" --memory 224m --memory-swap 224m --pids-limit 55     "${security[@]}" "${runtime[@]}" "${mounts[@]}"     --entrypoint /app/BotHost/Platform.BotHost "$image" --serve-sqlite-staging >/dev/null
docker start "$bot" >/dev/null
for attempt in {1..45}; do
    [[ $(docker inspect -f '{{.State.Running}}' "$bot") == true ]] || break
    docker logs "$bot" 2>&1 | grep -q 'managed SQLite staging bot is polling Telegram' && break
    sleep 1
done
[[ $(docker inspect -f '{{.State.Running}}' "$bot") == true ]] &&
    docker logs "$bot" 2>&1 | grep -q 'managed SQLite staging bot is polling Telegram' ||
    { printf 'Managed SQLite Telegram bot health check failed.\n' >&2; exit 1; }
docker exec "$api" sh -ec     'key=$(cat /stage/tester/api-key); test "$(curl -fsS --max-time 2 --noproxy "*" -H "X-Staging-Key: $key" http://127.0.0.1:18081/health/sqlite)" = "sqlite:ready"'

next=$(mktemp "$work/sqlite-active.XXXXXX")
printf '%s\n' "$api" "$worker" "$bot" "sha256:$digest" "$commit" > "$next"
chmod 0600 "$next"
mv -T "$next" "$previous"
succeeded=1
printf 'Persistent SQLite staging API, Telegram bot and worker are healthy at %s. No PostgreSQL rollback is permitted.\n' "$commit"
