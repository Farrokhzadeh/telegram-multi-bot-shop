#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

image=${1:?}
commit=${2:?}
qualification=${3:?}
[[ $# == 4 && $4 == --plaintext ]] || { printf 'SQLite staging requires an accepted plaintext runtime and --plaintext.\n' >&2; exit 1; }
format=2
[[ $image =~ ^docker\.io/[a-z0-9][a-z0-9_-]*/akanti@sha256:[a-f0-9]{64}$ ]]
[[ $commit =~ ^[a-f0-9]{40}$ ]]
[[ $qualification =~ ^akanti-staging-[a-f0-9]{20}$ ]]
work=/var/lib/akanti-staging
persistent=$work/sqlite
private=$work/sqlite-private
previous=$work/sqlite-active
source_root=$(cat "$work/shop-root")
[[ $source_root == /var/lib/teleg-cli/akanti-cold-staging-* && $(realpath -e "$source_root") == "$source_root" ]]
preview=$(cat "$source_root/telegram-preview.path")
[[ $(realpath -e "$preview") == "$source_root"/telegram-preview-* ]]
[[ $(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image") == "$commit" ]]
[[ $(docker inspect -f '{{.State.Running}}' "$qualification") == true ]]
[[ $(df -PB1 "$work" | awk 'NR==2 {print $4}') -ge 1610612736 ]]

lock=$work/sqlite.lock
exec 8>"$lock"
flock -n 8
[[ ! -L $persistent && ( ! -e $persistent || -d $persistent ) ]]

runtime_sqlite_format() {
	python3 - "$persistent/shop.db" "$format" <<'PY'
import pathlib, sqlite3, sys
path = pathlib.Path(sys.argv[1])
expected = int(sys.argv[2])
if path.is_symlink() or not path.is_file():
    raise SystemExit(1)
with path.open("rb") as file:
    header = file.read(100)
if len(header) != 100 or header[:16] != b"SQLite format 3\x00" or header[18:20] != b"\x02\x02":
    raise SystemExit(1)
wal, shm = (pathlib.Path(str(path) + suffix) for suffix in ("-wal", "-shm"))
journal = pathlib.Path(str(path) + "-journal")
if wal.is_symlink() or shm.is_symlink() or wal.exists() != shm.exists() or journal.exists() or journal.is_symlink():
    raise SystemExit(1)
has_wal = wal.exists()
if has_wal and (not wal.is_file() or not shm.is_file()):
    raise SystemExit(1)
options = "?mode=ro" if has_wal else "?mode=ro&immutable=1"
with sqlite3.connect(path.as_uri() + options, uri=True, timeout=2) as data:
    version = data.execute("PRAGMA user_version").fetchone()[0]
    application = data.execute("PRAGMA application_id").fetchone()[0]
    staged = data.execute("SELECT 1 FROM sqlite_schema WHERE name='_akanti_row_copy'").fetchone() is not None
    if (version, application) != (expected, 0) or staged:
        raise SystemExit(1)
    migrations = data.execute('SELECT MigrationId FROM "__EFMigrationsHistory" LIMIT 2').fetchall()
raise SystemExit(0 if migrations == [("20261126000000_InitialPlaintext",)] else 1)
PY
}

[[ -s $previous && ! -L $previous ]] || { printf 'Plaintext requires a separately accepted SQLite runtime.\n' >&2; exit 1; }
mapfile -t old < "$previous"
[[ ${#old[@]} == 6 && ${old[5]} == 2 &&
   ${old[0]} =~ ^akanti-sqlite-api-[a-zA-Z0-9-]+$ &&
   ${old[1]} =~ ^akanti-sqlite-worker-[a-zA-Z0-9-]+$ &&
   ${old[2]} =~ ^akanti-sqlite-bot-[a-zA-Z0-9-]+$ &&
   ${old[3]} =~ ^sha256:[a-f0-9]{64}$ && ${old[4]} =~ ^[a-f0-9]{40}$ ]]
old_api=${old[0]}
old_worker=${old[1]}
old_bot=${old[2]}
for prior in "$old_api" "$old_worker" "$old_bot"; do
    [[ $(docker inspect -f '{{.State.Running}} {{index .Config.Labels "org.akanti.schema-format"}}' "$prior") == 'true 2' ]]
done
[[ -d $persistent/supplier-evidence && ! -L $persistent/supplier-evidence ]]
old_asset_identity=$(stat -c '%d:%i' "$persistent/supplier-evidence")
runtime_sqlite_format || { printf 'SQLite state format differs from the accepted deployment.\n' >&2; exit 1; }
old_db_identity=$(stat -c '%d:%i' "$persistent/shop.db")
if [[ ${old[4]} == "$commit" ]]; then
    printf 'Persistent SQLite staging is already serving %s\n' "$commit"
    exit 0
fi

install -d -m 0700 "$persistent"
chown 10001:10001 "$persistent"
docker exec "$qualification" cat /app/sqlite-stage-provision.py > "$work/sqlite-stage-provision.py.next"
chmod 0500 "$work/sqlite-stage-provision.py.next"
python3 "$work/sqlite-stage-provision.py.next" --plaintext "$preview/config"
mv -f "$work/sqlite-stage-provision.py.next" "$work/sqlite-stage-provision.py"
[[ -f $private/tester/api-key ]]

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

same_accepted_sqlite() {
    runtime_sqlite_format && [[ $(stat -c '%d:%i' "$persistent/shop.db") == "$old_db_identity" ]] &&
        [[ ! -L $persistent/supplier-evidence &&
            $(stat -c '%d:%i' "$persistent/supplier-evidence") == "$old_asset_identity" ]]
}

switched=0
succeeded=0
cleanup() {
    local result=$?
    if (( succeeded )); then return 0; fi
    for name in "$bot" "$worker" "$api"; do
        docker rm -f "$name" >/dev/null 2>&1 || true
    done
    if (( switched )); then
        if same_accepted_sqlite &&
            docker start "$old_api" >/dev/null 2>&1 && docker start "$old_worker" >/dev/null 2>&1 &&
            docker start "$old_bot" >/dev/null 2>&1; then
            printf 'Restarted the previously qualified SQLite executable on its persistent database.\n' >&2
        else
            docker stop "$old_bot" "$old_worker" "$old_api" >/dev/null 2>&1 || true
            printf 'Same-database SQLite recovery could not be verified. Runtime stopped.\n' >&2
        fi
    fi
    exit "$result"
}
trap cleanup EXIT
trap 'exit 1' INT TERM

switched=1
docker stop --time 15 "$old_bot" >/dev/null
docker stop --time 15 "$old_worker" >/dev/null
docker stop --time 15 "$old_api" >/dev/null

mounts=(
    --mount "type=bind,src=$persistent,dst=/stage/sqlite"
    --mount "type=bind,src=$private/tester,dst=/stage/tester,readonly"
)
runtime=(
    --env INSTANCE_ID=staging
    --env "SHOP_SQLITE_STAGING=synthetic-acc-abad-v$format"
    --env SHOP_STAGING_SQLITE_PATH=/stage/sqlite/shop.db
)
security=(
    --pull never --read-only --cap-drop ALL --security-opt no-new-privileges
    --user 10001:10001 --cpus .35
    --tmpfs /tmp:rw,nosuid,nodev,size=16m,uid=10001,gid=10001
    --log-driver json-file --log-opt max-size=1m --log-opt max-file=2
)

docker create --name "$api" --label org.akanti.environment=staging-sqlite --label "org.akanti.schema-format=$format"     --network bridge --memory 256m --memory-swap 256m --pids-limit 60     "${security[@]}" "${runtime[@]}" "${mounts[@]}"         --entrypoint /app/Api/Platform.Api "$image" --serve-sqlite-staging >/dev/null
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

docker create --name "$worker" --label org.akanti.environment=staging-sqlite --label "org.akanti.schema-format=$format"     --network "container:$api" --memory 256m --memory-swap 256m --pids-limit 55     "${security[@]}" "${runtime[@]}" "${mounts[@]}"         --entrypoint /app/JobHost/Platform.JobHost "$image" --serve-sqlite-staging >/dev/null
docker start "$worker" >/dev/null
for attempt in {1..40}; do
    [[ $(docker inspect -f '{{.State.Running}}' "$worker") == true ]] || break
    docker logs "$worker" 2>&1 | grep -q 'managed SQLite staging delivery worker is running' && break
    sleep 1
done
[[ $(docker inspect -f '{{.State.Running}}' "$worker") == true ]] &&
    docker logs "$worker" 2>&1 | grep -q 'managed SQLite staging delivery worker is running' ||
    { printf 'Managed SQLite delivery worker health check failed.\n' >&2; exit 1; }

docker create --name "$bot" --label org.akanti.environment=staging-sqlite --label "org.akanti.schema-format=$format"     --network "container:$api" --memory 224m --memory-swap 224m --pids-limit 55     "${security[@]}" "${runtime[@]}" "${mounts[@]}"     --entrypoint /app/BotHost/Platform.BotHost "$image" --serve-sqlite-staging >/dev/null
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

docker exec "$api" /app/Api/Platform.Api --verify-sqlite-staging
same_accepted_sqlite

next=$(mktemp "$work/sqlite-active.XXXXXX")
printf '%s\n' "$api" "$worker" "$bot" "sha256:$digest" "$commit" > "$next"
if [[ $format == 2 ]]; then printf '2\n' >> "$next"; fi
chmod 0600 "$next"
if [[ -n $old_api ]]; then
    cp "$previous" "$work/sqlite-previous.next"
    chmod 0600 "$work/sqlite-previous.next"
    mv -T "$work/sqlite-previous.next" "$work/sqlite-previous"
fi
mv -T "$next" "$previous"
succeeded=1
printf 'Persistent SQLite staging API, Telegram bot and worker are healthy at %s. No PostgreSQL rollback is permitted.\n' "$commit"
