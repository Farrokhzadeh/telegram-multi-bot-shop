from pathlib import Path
from urllib import error, request as http
import json
import os
import sys
import time
from wallet_pilot import WalletPilot, request


class NoRedirect(http.HTTPRedirectHandler):
	def redirect_request(self, req, fp, code, msg, headers, newurl):
		return None


def transport(token):
	opener = http.build_opener(http.ProxyHandler({}), NoRedirect())
	base = "https://api.telegram.org/bot" + token + "/"

	def call(method, **params):
		request_body = json.dumps(params, separators=(",", ":")).encode()
		req = http.Request(base + method, request_body, {"Content-Type": "application/json"}, method="POST")
		try:
			with opener.open(req, timeout=15) as response:
				raw = response.read(65537)
		except error.HTTPError as exc:
			if method == "editMessageText" and exc.code == 400:
				data = json.loads(exc.read(4096))
				if data.get("description", "").startswith("Bad Request: message is not modified"):
					return True
			raise OSError("staging Telegram request rejected") from None
		except (error.URLError, TimeoutError):
			raise OSError("staging Telegram request incomplete") from None
		if len(raw) > 65536:
			raise ValueError("staging Telegram response too large")
		data = json.loads(raw)
		if data.get("ok") is not True:
			raise OSError("staging Telegram unsuccessful response")
		return data["result"]

	return call


def main():
	mode, root = sys.argv[1:]
	binding = json.loads(sys.stdin.read(1024))
	token, actor = binding["token"], binding["actor"]
	if not isinstance(token, str) or not 20 < len(token) <= 100 or type(actor) is not int or actor <= 0:
		raise ValueError("staging binding rejected")
	call = transport(token)
	bot = call("getMe")
	if bot.get("is_bot") is not True or bot.get("username", "").casefold() != "testadbadbot" or call("getWebhookInfo")["url"]:
		raise ValueError("dedicated staging polling bot required")
	if mode == "preflight":
		print("verified @testadbadbot polling identity", flush=True)
		return
	if mode != "poll":
		raise ValueError("invalid pilot mode")
	os.chdir(root)
	os.setgroups([])
	os.setgid(10001)
	os.setuid(10001)
	pilot = WalletPilot(actor, call, lambda command: request(Path("."), command))
	next_update = None
	deadline = time.monotonic() + 600
	opened_at = int(time.time()) - 2
	print("ready for allowlisted /checkoutpilot", flush=True)
	while time.monotonic() < deadline and not pilot.completed:
		try:
			args = {"timeout": 9, "limit": 10, "allowed_updates": ["message", "callback_query"]}
			if next_update is not None:
				args["offset"] = next_update
			updates = call("getUpdates", **args)
			for update in updates:
				next_update = update["update_id"] + 1
				message = update.get("message")
				if message and message.get("date", 0) < opened_at:
					continue
				try:
					pilot.update(update)
				except (OSError, ValueError, KeyError):
					print("pilot screen response uncertain", flush=True)
				if pilot.completed:
					break
		except OSError:
			time.sleep(2)
	if pilot.completed:
		print("allowlisted wallet smoke completed", flush=True)
	else:
		print("pilot window expired without a completed checkout", flush=True)


if __name__ == "__main__":
	try:
		main()
	except (OSError, ValueError, KeyError, IndexError):
		print("staging Telegram pilot rejected", file=sys.stderr, flush=True)
		raise SystemExit(2)
