#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

image=$1
commit=$2
qualification=$3
work=/var/lib/akanti-staging
root=$(cat "$work/shop-root")
[[ $root == /var/lib/teleg-cli/akanti-cold-staging-* && $(realpath -e "$root") == "$root" ]]
commerce=$(cat "$root/staging-commerce.path")
preview=$(cat "$root/telegram-preview.path")
[[ $(realpath -e "$commerce") == "$root"/commerce-* ]]
[[ $(realpath -e "$preview") == "$root"/* ]]
target=$commerce/target
db=akanti-staging-commerce-db
state=$work/shop-active
mapfile -t active < "$state"
old_core=${active[0]}
old_unit=${active[1]}
old_release=${active[2]}
old_commit=${active[3]}
[[ $old_core =~ ^akanti-staging-[a-zA-Z0-9-]+-core$ && $old_unit == "${old_core%-core}" ]]
[[ $(realpath -e "$old_release") == "$commerce"/* && $old_commit =~ ^[a-f0-9]{40}$ ]]
[[ $(docker inspect -f '{{.State.Running}} {{.HostConfig.NetworkMode}} {{len .HostConfig.PortBindings}} {{index .Config.Labels "org.akanti.environment"}}' "$db") == 'true none 0 staging-commerce' ]]
[[ $(docker ps --filter label=org.akanti.environment=staging-shop --format '{{.Names}}') == "$old_core" ]]
[[ -z $(docker ps -q --filter label=com.docker.compose.project=accabad-staging) ]]
systemctl is-active --quiet "$old_unit"
cmp -s "$work/shop-schema" <(docker exec "$qualification" cat /app/0001_core.sql) || {
	printf 'Staging shop schema differs from the candidate. Apply the matching staging migration first.\n' >&2
	exit 1
}

if [[ $commit == "$old_commit" ]]; then
	test -s "$target/ready.json" && test -s "$target/telegram-ready"
	printf 'Akanti staging Telegram bot already healthy at %s\n' "$commit"
	exit
fi

digest=${image##*:}
release=$(mktemp -d "$commerce/shop-${commit:0:12}.XXXXXX")
core=akanti-staging-shop-${digest:0:20}-${release##*.}-core
unit=${core%-core}
switched=0
created=0
next_state=

start_poller() {
	local name=$1 directory=$2 bot_host=$2/BotHost/Platform.BotHost
	if [[ ! -x $bot_host ]]; then bot_host=$directory/Platform.BotHost; fi
	systemd-run --unit="$name" --property=UMask=0077 --property=NoNewPrivileges=yes \
		--property=MemoryMax=160M --property=CPUQuota=30% --property=TasksMax=40 \
		--property=Restart=on-failure --property=RestartPreventExitStatus=78 --property=RestartSec=4 \
		--property="StandardOutput=append:$directory/telegram.private.log" \
		--property="StandardError=append:$directory/telegram.private.log" \
		--setenv=PYTHONDONTWRITEBYTECODE=1 --setenv=PYTHONUNBUFFERED=1 \
		--setenv="AKANTI_TELEGRAM_BOT_HOST=$bot_host" \
		/usr/bin/python3 "$directory/wallet_shop_telegram.py" "$preview/config" "$preview/state" "$target"
}

wait_ready() {
	local marker=$1 name=$2 attempt
	for (( attempt=0; attempt<60; attempt++ )); do
		if [[ $marker == telegram-ready ]]; then
			systemctl is-active --quiet "$name" || return 1
		else
			[[ $(docker inspect -f '{{.State.Running}}' "$name") == true ]] || return 1
		fi
		[[ -s $target/$marker ]] && return 0
		sleep 1
	done
	return 1
}

rollback() {
	local status=$?
	if (( switched )); then
		systemctl stop "$unit" >/dev/null 2>&1 || true
		if (( created )); then docker rm -f "$core" >/dev/null || true; fi
		rm -f "$target/ready.json" "$target/telegram-ready"
		if docker start "$old_core" >/dev/null &&
			wait_ready ready.json "$old_core" &&
			{ systemctl is-active --quiet "$old_unit" || start_poller "$old_unit" "$old_release"; } &&
			wait_ready telegram-ready "$old_unit"; then
			printf 'Restored staging Telegram bot at %s\n' "$old_commit" >&2
		else
			printf 'Staging Telegram bot rollback failed for %s\n' "$old_commit" >&2
		fi
	fi
	if [[ -n $next_state ]]; then rm -f "$next_state"; fi
	exit "$status"
}
trap rollback EXIT
trap 'exit 1' INT TERM

docker cp "$qualification:/app/shop/." "$release"
if docker exec "$qualification" test -d /app/BotHost; then
	docker cp "$qualification:/app/BotHost" "$release/BotHost"
	bot_host=$release/BotHost/Platform.BotHost
	core_entry=/app/AotSmoke/Platform.AotSmoke
else
	docker cp "$qualification:/app/Platform.BotHost" "$release/Platform.BotHost"
	bot_host=$release/Platform.BotHost
	core_entry=/app/Platform.AotSmoke
fi
if [[ -f $work/accabad-stage-shop.json ]]; then
	install -m 0444 "$old_release/accabad_stage.py" "$old_release/wallet_shop_telegram.py" "$release/"
fi
"$bot_host" --qualify-ingress
next_state=$(mktemp "$work/shop-active.XXXXXX")
printf '%s\n' "$core" "$unit" "$release" "$commit" > "$next_state"

switched=1
systemctl stop "$old_unit"
docker stop --time 7 "$old_core" >/dev/null
rm -f "$target/ready.json" "$target/telegram-ready"
docker create --pull never --name "$core" --label org.akanti.environment=staging-shop --restart unless-stopped \
	--user 10001:10001 --network "container:$db" --read-only --cap-drop ALL --security-opt no-new-privileges \
	--cpus .6 --memory 300m --memory-swap 300m --pids-limit 60 --tmpfs /tmp:rw,nosuid,size=24m,uid=10001,gid=10001 \
	--env-file "$commerce/runtime.env" --mount "type=bind,src=$target,dst=/stage/target" \
	--workdir /stage/target --log-opt max-size=1m --log-opt max-file=2 \
	--entrypoint "$core_entry" "$image" --cutover-stage --root /stage/target --mode serve >/dev/null
created=1
docker start "$core" >/dev/null
wait_ready ready.json "$core"
start_poller "$unit" "$release"
wait_ready telegram-ready "$unit"
mv -T "$next_state" "$state"
switched=0
printf '%s\n' "$commit" > "$commerce/telegram-current-commit"
printf 'Akanti staging Telegram bot is healthy at %s (%s)\n' "$commit" "$image"
