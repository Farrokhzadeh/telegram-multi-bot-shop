#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

work=/var/lib/akanti-staging
active=$work/sqlite-active
previous=$work/sqlite-previous
persistent=$work/sqlite/shop.db
[[ $EUID == 0 && -f $persistent && -s $active && -s $previous ]]
exec 8>"$work/sqlite.lock"
flock -n 8
mapfile -t current < "$active"
mapfile -t restore < "$previous"
for item in current restore; do
    declare -n rows="$item"
    [[ ${#rows[@]} == 5 &&
        ${rows[0]} =~ ^akanti-sqlite-api-[a-zA-Z0-9-]+$ &&
        ${rows[1]} =~ ^akanti-sqlite-worker-[a-zA-Z0-9-]+$ &&
        ${rows[2]} =~ ^akanti-sqlite-bot-[a-zA-Z0-9-]+$ &&
        ${rows[3]} =~ ^sha256:[a-f0-9]{64}$ &&
        ${rows[4]} =~ ^[a-f0-9]{40}$ ]]
    for name in "${rows[0]}" "${rows[1]}" "${rows[2]}"; do
        [[ $(docker inspect -f '{{index .Config.Labels "org.akanti.environment"}}' "$name") == staging-sqlite ]]
    done
done
[[ ${current[0]} != "${restore[0]}" ]]
[[ $(docker inspect -f '{{.State.Running}}' "${current[0]}") == true &&
   $(docker inspect -f '{{.State.Running}}' "${current[1]}") == true &&
   $(docker inspect -f '{{.State.Running}}' "${current[2]}") == true ]]
[[ $(docker inspect -f '{{.State.Running}}' "${restore[0]}") == false &&
   $(docker inspect -f '{{.State.Running}}' "${restore[1]}") == false &&
   $(docker inspect -f '{{.State.Running}}' "${restore[2]}") == false ]]
before=$(stat -c '%d:%i' "$persistent")
reverted=0
rescue() {
    local status=$?
    if (( reverted )); then return 0; fi
    docker stop --time 10 "${restore[2]}" "${restore[1]}" "${restore[0]}" >/dev/null 2>&1 || true
    docker start "${current[0]}" >/dev/null 2>&1 || true
    docker start "${current[1]}" >/dev/null 2>&1 || true
    docker start "${current[2]}" >/dev/null 2>&1 || true
    printf 'Previous SQLite revision could not be resumed; current revision restarted on unchanged database.\n' >&2
    exit "$status"
}
trap rescue EXIT
trap 'exit 1' INT TERM
docker stop --time 15 "${current[2]}" "${current[1]}" "${current[0]}" >/dev/null
docker start "${restore[0]}" >/dev/null
ready=0
for attempt in {1..35}; do
    if docker exec "${restore[0]}" sh -ec         'key=$(cat /stage/tester/api-key); test "$(curl -fsS --max-time 2 --noproxy "*" -H "X-Staging-Key: $key" http://127.0.0.1:18081/health/sqlite)" = "sqlite:ready"' >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 1
done
[[ $ready == 1 ]]
docker exec "${restore[0]}" /app/Api/Platform.Api --verify-sqlite-staging
docker start "${restore[1]}" >/dev/null
docker start "${restore[2]}" >/dev/null
sleep 4
[[ $(docker inspect -f '{{.State.Running}}' "${restore[1]}") == true &&
   $(docker inspect -f '{{.State.Running}}' "${restore[2]}") == true ]]
[[ $(stat -c '%d:%i' "$persistent") == "$before" ]]
next_current=$(mktemp "$work/sqlite-active.XXXXXX")
next_previous=$(mktemp "$work/sqlite-previous.XXXXXX")
cp "$previous" "$next_current"
cp "$active" "$next_previous"
chmod 0600 "$next_current" "$next_previous"
mv -T "$next_current" "$active"
mv -T "$next_previous" "$previous"
reverted=1
printf 'Previous SQLite code restored with the same database inode and all accepted SQLite writes intact. PostgreSQL was not restarted.\n'
