"""Staging-only synthetic encrypted order-history bridge for legacy polling bot.

Installed only in the testadbadbot staging image. Never used for production.
The actor and private chat come from Telegram's authenticated polling update.
"""
import asyncio
import json
import logging
import os
import pathlib
import subprocess

ROOT = pathlib.Path("/app/data/teleg-staging-history-v1")
BINARY = pathlib.Path("/app/data/teleg-staging-runtime/Platform.BotHost")
LOGGER = logging.getLogger("staging.history")


def _request(actor, bot_id, command):
    """Invoke the new native renderer with exact identity and bounded output."""
    completed = subprocess.run(
        [
            str(BINARY), "--staging-history-render", "--root", str(ROOT),
            "--actor", str(actor), "--bot", str(bot_id), "--command", command,
        ],
        capture_output=True, text=True, check=True, timeout=8,
    )
    if len(completed.stdout) > 20000:
        raise ValueError("Staging history response too large")
    data = json.loads(completed.stdout)
    if set(data) != {"messages", "nextOffset", "sourceOrders", "syntheticOnly"}:
        raise ValueError("Invalid staging history response")
    if data["sourceOrders"] != 55 or data["syntheticOnly"] is not True:
        raise ValueError("Staging history must remain synthetic")
    messages = data["messages"]
    if not isinstance(messages, list) or not (1 <= len(messages) <= 5):
        raise ValueError("Invalid staging history page")
    if not all(isinstance(m, str) and 0 < len(m) <= 3900 and
               m.startswith("[STAGING TEST - SYNTHETIC ORDERS ONLY]") for m in messages):
        raise ValueError("Invalid staging history text")
    return messages


def _bound_actor():
    binding = ROOT / "stage-binding.json"
    if not binding.is_file() or binding.is_symlink() or binding.stat().st_mode & 0o077:
        return None
    data = json.loads(binding.read_text())
    if data.get("instanceId") != "staging" or data.get("botUsername") != "testadbadbot" or data.get("syntheticOnly") is not True:
        raise ValueError("Staging binding rejected")
    actor = data.get("actorId")
    return actor if type(actor) is int and actor > 0 else None


async def maybe_reply_history(update, context, argument, actor, admins):
    """Returns True only for the single bound staging administrator."""
    if os.getenv("INSTANCE_ID") != "staging" or actor not in admins:
        return False
    if not ROOT.is_dir() or not BINARY.is_file():
        return False
    try:
        bound = _bound_actor()
    except (OSError, ValueError, TypeError):
        return False
    if bound != actor:
        return False
    message = update.message
    chat = update.effective_chat
    sender = update.effective_user
    if (message is None or chat is None or sender is None or
            chat.type != "private" or chat.id != actor or sender.id != actor):
        LOGGER.warning("history_ingress_rejected_nonprivate")
        return True
    command = "/orders" + (" " + argument if argument else "")
    try:
        bot = await context.bot.get_me()
        if not bot.is_bot or (bot.username or "").lower() != "testadbadbot":
            raise ValueError("Not the staging bot")
        messages = await asyncio.to_thread(_request, actor, bot.id, command)
    except (OSError, subprocess.SubprocessError, ValueError, TypeError):
        LOGGER.warning("history_render_rejected")
        await message.reply_text(
            "[STAGING TEST] Synthetic history unavailable; no live orders were accessed.",
            parse_mode=None, protect_content=True, disable_notification=True,
        )
        return True
    for index, text in enumerate(messages, start=1):
        LOGGER.info("history_send_attempt segment=%d", index)
        await message.reply_text(
            text, parse_mode=None, protect_content=True, disable_notification=True,
        )
        LOGGER.info("history_send_acknowledged segment=%d", index)
    LOGGER.info("history_response_complete segments=%d", len(messages))
    return True
