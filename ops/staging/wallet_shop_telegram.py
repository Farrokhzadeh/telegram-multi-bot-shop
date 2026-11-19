"""Serve the staging wallet shop using the existing synthetic checkout bridge."""

from pathlib import Path
import os
import sys
import time

from wallet_pilot import WalletShop, request
from wallet_pilot_telegram import transport


def run(config, state, target):
    token = (config / "bot-token").read_text().strip()
    bot_id = int((config / "bot-id").read_text())
    tester = int((config / "tester-id").read_text())
    if not token.startswith(f"{bot_id}:") or tester <= 0:
        raise ValueError("Invalid staging bot binding")
    call = transport(token)
    bot = call("getMe")
    if (bot["id"] != bot_id or bot["username"] != "testadbadbot" or
            bot["is_bot"] is not True or call("getWebhookInfo")["url"]):
        raise ValueError("Staging bot must own its Telegram poll")
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
            try:
                pilot.update(update)
                if pilot.completed:
                    print("Staging wallet purchase confirmed.", flush=True)
                    pilot.completed = False
            except (OSError, ValueError, KeyError, TimeoutError):
                print("A staging shop response was inconclusive.", flush=True)
            offset = identifier + 1
            checkpoint = Path("telegram-offset.pending")
            checkpoint.write_text(str(offset) + "\n")
            checkpoint.replace(cursor)


if __name__ == "__main__":
    os.umask(0o077)
    try:
        run(*(Path(value) for value in sys.argv[1:]))
    except (OSError, ValueError, KeyError, TypeError):
        print("Staging Telegram shop startup rejected.", file=sys.stderr)
        raise SystemExit(78)
