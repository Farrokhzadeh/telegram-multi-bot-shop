"""Connect staging scripts to Akanti's Telegram.Bot transport over private pipes."""

import atexit
import json
import os
from pathlib import Path
import subprocess


class TelegramError(OSError):
    def __init__(self, status):
        super().__init__("Staging Telegram request rejected")
        self.code = status


class TelegramTransport:
    def __init__(self, token):
        binary = Path(os.environ["AKANTI_TELEGRAM_BOT_HOST"])
        if not binary.is_absolute() or not binary.is_file() or not os.access(binary, os.X_OK):
            raise ValueError("An absolute Akanti BotHost executable is required")
        if not isinstance(token, str) or not 24 <= len(token) <= 128 or "\n" in token or "\r" in token:
            raise ValueError("Invalid Telegram token")
        self.process = subprocess.Popen(
            [str(binary), "--staging-telegram-stdio"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8",
        )
        try:
            self.process.stdin.write(token + "\n")
            self.process.stdin.flush()
        except OSError:
            self.close()
            raise OSError("Staging Telegram transport could not start") from None
        atexit.register(self.close)

    def __call__(self, method, **parameters):
        try:
            line = json.dumps({"method": method, "parameters": parameters}, separators=(",", ":"))
            self.process.stdin.write(line + "\n")
            self.process.stdin.flush()
            reply = self.process.stdout.readline(1048577)
        except (OSError, ValueError):
            raise OSError("Staging Telegram request incomplete") from None
        if not 0 < len(reply) <= 1048576 or not reply.endswith("\n"):
            raise OSError("Staging Telegram transport returned an incomplete response")
        try:
            response = json.loads(reply)
        except (json.JSONDecodeError, UnicodeError):
            raise OSError("Staging Telegram transport returned invalid data") from None
        if response.get("ok") is not True:
            if type(response.get("status")) is int:
                raise TelegramError(response["status"])
            raise TimeoutError("Staging Telegram request incomplete")
        return response["result"]

    def close(self):
        if self.process.stdin and not self.process.stdin.closed:
            try:
                self.process.stdin.close()
            except BrokenPipeError:
                pass
        try:
            self.process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        if self.process.stdout:
            self.process.stdout.close()
