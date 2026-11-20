import unittest
import uuid

from accabad_presentation import TextPresentation
from telegram_transport import TelegramError

from wallet_pilot import WalletShop
from wallet_shop_telegram import legacy_history_chat


class WalletShopTests(unittest.TestCase):
    def setUp(self):
        self.id = str(uuid.uuid4())
        self.ref = uuid.UUID(self.id).hex
        self.profile = {"language": "en", "balance": 100, "orders": 0, "joined": "2026-01-01", "contact": None}
        self.record = {"id": self.id, "title": "Local product", "status": "ready", "quantity": 1,
                       "amount": 10, "currency": "USD", "created": "2026-01-01"}
        self.transport_calls = []
        self.operations = []
        self.shop = WalletShop(101, self.transport, self.checkout)
        self.message = {"message_id": 11, "from": {"id": 101}, "chat": {"id": 101, "type": "private"}}

    def checkout(self, operation):
        self.operations.append(operation)
        if operation.startswith("pilot-screen:language:"):
            self.profile["language"] = operation.rsplit(":", 1)[1]
        if operation.startswith(("pilot-screen:home", "pilot-screen:language:")):
            return {"outcome": "Home", "home": self.profile}
        if operation.startswith("pilot-screen:list:"):
            label = operation.split(":")[2]
            return {"outcome": "Page", "page": {"entries": [self.record] if label == "Orders" else [], "hasNext": False}}
        if operation.startswith("pilot-screen:detail:Orders:"):
            return {"outcome": "Detail", "entry": self.record}
        if operation == "pilot-wallet":
            return {"outcome": "Wallet", "balance": 100}
        if operation == "pilot-catalog":
            return {"outcome": "Available", "title": "Local product", "amount": 10, "minimum": 1, "maximum": 3}
        if operation == "pilot-quote:1":
            return {"outcome": "Quoted", "order": self.id, "quantity": 1, "amount": 10, "currency": "USD", "title": "Local product"}
        if operation == "pilot-buy:" + self.ref:
            return {"outcome": "Purchased", "order": self.id}
        if operation == "manifest-worker":
            return {"outcome": "PreparedWithoutSend"}
        if operation == "pilot-reveal:" + self.ref:
            return {"outcome": "Items", "items": ["test item"]}
        raise AssertionError(operation)

    def transport(self, method, **payload):
        self.transport_calls.append((method, payload))
        return {"message_id": 11}

    def send(self, text):
        self.shop.update({"message": {**self.message, "text": text}})

    def button(self, value, actor=101):
        self.shop.update({"callback_query": {"id": "test", "message": self.message,
                                            "from": {"id": actor}, "data": "t079:" + value}})

    def text(self):
        payload = next(payload for method, payload in reversed(self.transport_calls) if method in
                       ("sendMessage", "sendRichMessage", "editMessageText"))
        if "rich_message" in payload:
            return TextPresentation(payload["rich_message"]["html"]).text()
        return payload["text"]

    def test_legacy_history_requires_the_approved_private_sender(self):
        history = {"actor": 101, "bot": 102, "text": "Historical orders"}
        sender = {"id": 101, "is_bot": False}
        message = {**self.message, "from": sender, "text": "/legacyorders@testadbadbot"}
        self.assertEqual(101, legacy_history_chat({"message": message}, history))
        self.assertIsNone(legacy_history_chat({"message": message}, None))
        self.assertIsNone(legacy_history_chat({"callback_query": {"message": message}}, history))
        self.assertIsNone(legacy_history_chat({"message": {**message, "from": {**sender, "id": 103}}}, history))
        self.assertIsNone(legacy_history_chat({"message": {**message, "from": {**sender, "is_bot": True}}}, history))
        self.assertIsNone(legacy_history_chat({"message": {**message, "chat": {"id": 101, "type": "group"}}}, history))

    def test_customer_navigation_and_persisted_language(self):
        self.send("/start")
        self.assertIn("Akanti", self.text())
        self.button("wallet")
        self.assertIn("USDT | 100.00", self.text())
        self.button("transactions:0")
        self.assertIn("pilot-screen:list:Transactions:all:0", self.operations)
        self.button("deposits:0")
        self.assertIn("pilot-screen:list:Deposits:all:0", self.operations)
        self.button("discounts:0")
        self.assertIn("pilot-screen:list:Discounts:all:0", self.operations)
        self.button("support")
        self.button("support_orders:active:0")
        self.assertIn("pilot-screen:list:Orders:active:0", self.operations)
        self.button("language")
        self.button("setlang:fa", actor=102)
        self.assertEqual("en", self.profile["language"])
        self.button("setlang:fa")
        self.assertIn("موجودی", self.text())
        self.send("/profile")
        self.assertIn("موجودی", self.text())

    def test_purchase_order_and_reveal_are_wired_with_no_repeat_debit(self):
        self.send("/shop")
        self.button("product")
        self.assertIn("$10.00", self.text())
        self.button("quantity")
        self.send("۱")
        self.assertIn("SELECT PAYMENT METHOD", self.text())
        self.button("pay")
        self.button("pay")
        self.assertEqual(1, self.operations.count("pilot-buy:" + self.ref))
        self.button("order:" + self.ref)
        self.assertIn("Purchase receipt", self.text())
        self.button("reveal:" + self.ref)
        self.assertEqual("test item", self.text())
        self.button("order:" + self.ref)
        self.button("orders:finished:0")
        self.assertIn("pilot-screen:list:Orders:finished:0", self.operations)

    def test_home_preserves_legacy_tables_button_rows_and_styles(self):
        self.send("/start")
        method, payload = self.transport_calls[-1]
        self.assertEqual("sendRichMessage", method)
        self.assertEqual(3, payload["rich_message"]["html"].count("<table bordered striped>"))
        rows = payload["reply_markup"]["inline_keyboard"]
        self.assertEqual([["BROWSE SHOP"], ["My Orders"], ["My Wallet", "My Discounts"],
                          ["Support Desk", "Change Language"]], [[b["text"] for b in row] for row in rows])
        self.assertEqual("success", rows[0][0]["style"])
        self.assertEqual("primary", rows[1][0]["style"])
        self.assertEqual("6082290027532918112", rows[0][0]["icon_custom_emoji_id"])

    def test_rich_fallback_requires_a_definite_rejection(self):
        original = self.shop.call

        def reject_rich(method, **payload):
            if method == "sendRichMessage":
                raise TelegramError(400)
            return original(method, **payload)

        self.shop.call = reject_rich
        self.send("/start")
        method, payload = self.transport_calls[-1]
        self.assertEqual("sendMessage", method)
        self.assertEqual("HTML", payload["parse_mode"])
        self.assertNotIn("icon_custom_emoji_id", payload["reply_markup"]["inline_keyboard"][0][0])

        def uncertain(method, **payload):
            self.transport_calls.append((method, payload))
            raise TimeoutError()

        self.shop.call = uncertain
        self.transport_calls.clear()
        with self.assertRaises(TimeoutError):
            self.send("/start")
        self.assertEqual(["sendRichMessage"], [method for method, _ in self.transport_calls])


if __name__ == "__main__":
    unittest.main()
