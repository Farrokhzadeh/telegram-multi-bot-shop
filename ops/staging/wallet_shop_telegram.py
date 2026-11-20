"""Serve the staging wallet shop using the existing synthetic checkout bridge."""

from pathlib import Path
import json
import os
import stat
import sys
import time

from wallet_pilot import WalletShop, request
from telegram_transport import TelegramTransport


def load_history(path, bot_id):
	try:
		fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
	except FileNotFoundError:
		return None
	with os.fdopen(fd, "rb") as stream:
		info = os.fstat(stream.fileno())
		if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o077 or info.st_nlink != 1 or not 0 < info.st_size <= 16384:
			raise ValueError("Invalid AccAbad history file")
		history = json.loads(stream.read(16385))
	if (set(history) != {"actor", "bot", "text"} or type(history["actor"]) is not int or history["actor"] <= 0
			or type(history["bot"]) is not int or history["bot"] != bot_id or not isinstance(history["text"], str)
			or not 0 < len(history["text"].encode("utf-16-le")) <= 7000):
		raise ValueError("Invalid AccAbad history binding")
	return history


def legacy_history_chat(update, history):
	message = update.get("message") or {}
	actor = message.get("from") or {}
	chat = message.get("chat") or {}
	if (history and message.get("text") in ("/legacyorders", "/legacyorders@testadbadbot")
			and actor.get("is_bot") is False and type(actor.get("id")) is int
			and actor["id"] == history["actor"] == chat.get("id") and chat.get("type") == "private"):
		return actor["id"]
	return None


def save_offset(cursor, offset):
	checkpoint = Path("telegram-offset.pending")
	checkpoint.write_text(str(offset) + "\n")
	checkpoint.replace(cursor)


def run(config, state, target):
    token = (config / "bot-token").read_text().strip()
    bot_id = int((config / "bot-id").read_text())
    tester = int((config / "tester-id").read_text())
    if not token.startswith(f"{bot_id}:") or tester <= 0:
        raise ValueError("Invalid staging bot binding")
    call = TelegramTransport(token)
    bot = call("getMe")
    if (bot["id"] != bot_id or bot["username"] != "testadbadbot" or
            bot["is_bot"] is not True or call("getWebhookInfo")["url"]):
        raise ValueError("Staging bot must own its Telegram poll")
    history = load_history(Path("/var/lib/akanti-staging/accabad-history.json"), bot_id)
    previous = state / "next-offset"
    offset = int(previous.read_text()) if previous.exists() else 0

    os.chdir(target)
    os.setgroups([])
    os.setgid(10001)
    os.setuid(10001)
    cursor = Path("telegram-offset")
    if cursor.exists():
        offset = max(offset, int(cursor.read_text()))
    ready = Path("telegram-ready")
    ready.unlink(missing_ok=True)
    pilot = WalletShop(tester, call, lambda command: request(Path("."), command))
    while True:
        try:
            updates = call("getUpdates", offset=offset, timeout=9, limit=10,
                           allowed_updates=["message", "callback_query"])
        except OSError:
            ready.unlink(missing_ok=True)
            time.sleep(2)
            continue
        if not ready.exists():
            ready.write_text("ready\n")
            print("Akanti staging shop is polling Telegram.", flush=True)
        for update in updates:
            identifier = update.get("update_id")
            if type(identifier) is not int or identifier < offset:
                continue
            chat = legacy_history_chat(update, history)
            if chat is not None:
                offset = identifier + 1
                save_offset(cursor, offset)
                try:
                    call("sendMessage", chat_id=chat, text=history["text"], protect_content=True)
                except (OSError, ValueError, KeyError, TimeoutError):
                    print("An AccAbad history response was inconclusive.", flush=True)
                continue
            try:
                pilot.update(update)
                if pilot.completed:
                    print("Staging wallet purchase confirmed.", flush=True)
                    pilot.completed = False
            except (OSError, ValueError, KeyError, TimeoutError):
                print("A staging shop response was inconclusive.", flush=True)
            offset = identifier + 1
            save_offset(cursor, offset)


if __name__ == "__main__":
    os.umask(0o077)
    try:
        run(*(Path(value) for value in sys.argv[1:]))
    except (OSError, ValueError, KeyError, TypeError):
        print("Staging Telegram shop startup rejected.", file=sys.stderr)
        raise SystemExit(78)
