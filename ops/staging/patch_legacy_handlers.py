"""Build-time staging-only overlay patch; never edits legacy source or production images."""
import pathlib
import sys

ANCHOR = '    elif cmd == "/orders":\n        text, markup, rich = await orders_menu(u, context)'
REPLACEMENT = '''    elif cmd == "/orders":
        if update.effective_chat and update.effective_chat.type == "private":
            from .staging_history_bridge import maybe_reply_history
            if await maybe_reply_history(update, context, arg, u[0], ADMINS):
                return
        text, markup, rich = await orders_menu(u, context)'''


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: stage_patch.py STAGING_OVERLAY_HANDLERS")
    target = pathlib.Path(sys.argv[1])
    code = target.read_text()
    if code.count(ANCHOR) != 1 or "from .staging_history_bridge" in code:
        raise SystemExit("Legacy handler layout changed; refuse staging overlay")
    target.write_text(code.replace(ANCHOR, REPLACEMENT))
    compile(target.read_text(), str(target), "exec")
    print("PASS: exact staging-only /orders overlay compiled")


if __name__ == "__main__":
    main()
