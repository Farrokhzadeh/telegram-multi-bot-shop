"""Isolated no-network tests for the staging-only legacy shim."""
import asyncio
import importlib.util
import os
import pathlib
import tempfile
import types
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("staging_history_bridge", pathlib.Path(__file__).with_name("staging_history_bridge.py"))
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


class StageOnlyHistoryTests(unittest.TestCase):
    def update(self, actor=7001, chat_type="private"):
        messages = []
        async def reply(text, **kwargs):
            messages.append((text, kwargs))
        upd = types.SimpleNamespace(
            message=types.SimpleNamespace(reply_text=reply),
            effective_user=types.SimpleNamespace(id=actor),
            effective_chat=types.SimpleNamespace(id=actor, type=chat_type),
        )
        context = types.SimpleNamespace(
            bot=types.SimpleNamespace(get_me=lambda: asyncio.sleep(0, result=types.SimpleNamespace(
                id=1001, username="testadbadbot", is_bot=True
            )))
        )
        return upd, context, messages

    def test_wrong_instance_never_calls_binary(self):
        upd, ctx, messages = self.update()
        with patch.dict(os.environ, {"INSTANCE_ID": "prod1"}):
            self.assertFalse(asyncio.run(module.maybe_reply_history(upd, ctx, "", 7001, [7001])))
        self.assertFalse(messages)

    def test_only_bound_staging_member_receives_synthetic_data(self):
        upd, ctx, messages = self.update()
        with (patch.dict(os.environ, {"INSTANCE_ID": "staging"}),
              patch.object(module, "ROOT", types.SimpleNamespace(is_dir=lambda: True)),
              patch.object(module, "BINARY", types.SimpleNamespace(is_file=lambda: True)),
              patch.object(module, "_bound_actor", return_value=7001),
              patch.object(module, "_request", return_value=[
                  "[STAGING TEST - SYNTHETIC ORDERS ONLY]\nHistorical orders (read-only)"
              ]) as cli):
            self.assertTrue(asyncio.run(module.maybe_reply_history(upd, ctx, "20", 7001, [7001])))
            cli.assert_called_once_with(7001, 1001, "/orders 20")
            self.assertEqual(len(messages), 1)
            self.assertIsNone(messages[0][1]["parse_mode"])
            self.assertTrue(messages[0][1]["protect_content"])
        upd, ctx, messages = self.update(7002)
        with (patch.dict(os.environ, {"INSTANCE_ID": "staging"}),
              patch.object(module, "ROOT", types.SimpleNamespace(is_dir=lambda: True)),
              patch.object(module, "BINARY", types.SimpleNamespace(is_file=lambda: True)),
              patch.object(module, "_bound_actor", return_value=7001)):
            self.assertFalse(asyncio.run(module.maybe_reply_history(upd, ctx, "", 7002, [7002])))
        self.assertFalse(messages)

    def test_staging_history_audit_contains_no_identifiers_or_content(self):
        upd, ctx, messages = self.update()
        with (patch.dict(os.environ, {"INSTANCE_ID": "staging"}),
              patch.object(module, "ROOT", types.SimpleNamespace(is_dir=lambda: True)),
              patch.object(module, "BINARY", types.SimpleNamespace(is_file=lambda: True)),
              patch.object(module, "_bound_actor", return_value=7001),
              patch.object(module, "_request", return_value=[
                  "[STAGING TEST - SYNTHETIC ORDERS ONLY]\\nSynthetic private data"
              ]),
              self.assertLogs("staging.history", level="INFO") as log):
            self.assertTrue(asyncio.run(module.maybe_reply_history(upd, ctx, "", 7001, [7001])))
        audit = "\\n".join(log.output)
        self.assertIn("history_send_attempt", audit)
        self.assertIn("history_send_acknowledged", audit)
        self.assertIn("history_response_complete", audit)
        self.assertNotIn("7001", audit)
        self.assertNotIn("Synthetic private data", audit)

    def test_fail_closed_if_stage_command_rejected(self):
        upd, ctx, messages = self.update()
        with (patch.dict(os.environ, {"INSTANCE_ID": "staging"}),
              patch.object(module, "ROOT", types.SimpleNamespace(is_dir=lambda: True)),
              patch.object(module, "BINARY", types.SimpleNamespace(is_file=lambda: True)),
              patch.object(module, "_bound_actor", return_value=7001),
              patch.object(module, "_request", side_effect=ValueError())):
            self.assertTrue(asyncio.run(module.maybe_reply_history(upd, ctx, "garbage", 7001, [7001])))
        self.assertEqual(len(messages), 1)
        self.assertIn("unavailable", messages[0][0])


if __name__ == "__main__":
    unittest.main()
