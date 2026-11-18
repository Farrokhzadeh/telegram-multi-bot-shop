#!/usr/bin/env bash
set -Eeuo pipefail

image=${1:-}
commit=${2:-}
[[ $image =~ ^docker\.io/[a-z0-9][a-z0-9_-]*/akanti(@sha256:[a-f0-9]{64})$ ]]
[[ $commit =~ ^[a-f0-9]{40}$ ]]
digest=${image##*:}
candidate="akanti-staging-${digest:0:20}"
label=org.akanti.environment=staging-qualification
work=/var/lib/akanti-staging

install -d -m 0700 "$work"
exec 9>"$work/deploy.lock"
flock -n 9

[[ $(uname -m) == x86_64 ]]
[[ $(df -PB1 "$work" | awk 'NR==2 {print $4}') -ge 3221225472 ]]
[[ $(awk '/^MemAvailable:/ {print $2}' /proc/meminfo) -ge 1048576 ]]
[[ $(nproc) -ge 2 ]]

docker pull --platform linux/amd64 "$image"
[[ $(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image") == "$commit" ]]
expected_image=$(docker image inspect --format '{{.Id}}' "$image")

created=0
rollback() {
    if (( created )); then docker rm -f "$candidate" >/dev/null || true; fi
}
trap rollback EXIT
if docker container inspect "$candidate" >/dev/null 2>&1; then
    [[ $(docker inspect --format '{{index .Config.Labels "org.akanti.environment"}} {{.Image}}' "$candidate") == "staging-qualification $expected_image" ]]
    docker start "$candidate" >/dev/null
else
    docker run -d --name "$candidate" --label "$label" --pull never \
        --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
        --user 10001:10001 --tmpfs /tmp:rw,nosuid,nodev,size=16m --cpus .10 \
        --memory 128m --memory-swap 128m --pids-limit 55 \
        --log-driver json-file --log-opt max-size=1m --log-opt max-file=2 \
        --restart unless-stopped "$image" >/dev/null
    created=1
fi

ready=0
for attempt in {1..20}; do
    if reply=$(docker exec "$candidate" curl --fail --silent --max-time 2 --noproxy '*' \
        -H 'X-Qualification-Token: public-synthetic-probe' http://127.0.0.1:18080/qualification) &&
        [[ $reply == '{"host":"Api","mode":"synthetic-qualification","applicationReady":false}' ]]; then
        ready=1
        break
    fi
    sleep 1
done
if (( ! ready )); then
    printf 'Akanti staging health check failed for %s\n' "$commit" >&2
    exit 1
fi

created=0
previous=$(docker ps -a --filter "label=$label" --format '{{.Names}}')
for name in $previous; do
    if [[ $name != "$candidate" && $name =~ ^akanti-staging-[a-f0-9]{20}$ ]]; then
        docker stop --time 10 "$name" >/dev/null
        docker rm "$name" >/dev/null
    fi
done
printf 'Akanti staging qualification is healthy at %s (%s)\n' "$commit" "$image"
