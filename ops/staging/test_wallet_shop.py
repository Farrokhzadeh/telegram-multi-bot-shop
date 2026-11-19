import unittest
import uuid

from wallet_pilot import WalletShop


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
        if operation == "pilot-catalog":
            return {"outcome": "Available", "title": "Local product", "amount": 10, "minimum": 1, "maximum": 3}
        if operation == "pilot-quote:1":
            return {"outcome": "Quoted", "order": self.id, "quantity": 1, "amount": 10, "currency": "USD"}
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
        return self.transport_calls[-1][1]["text"]

    def test_customer_navigation_and_persisted_language(self):
        self.send("/start")
        self.assertIn("Akanti", self.text())
        self.button("wallet")
        self.assertIn("100 USD", self.text())
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
        self.assertIn("10 USD", self.text())
        self.button("quantity")
        self.send("۱")
        self.assertIn("Total: 10 USD", self.text())
        self.button("pay")
        self.button("pay")
        self.assertEqual(1, self.operations.count("pilot-buy:" + self.ref))
        self.button("order:" + self.ref)
        self.assertIn("Ready", self.text())
        self.button("reveal:" + self.ref)
        self.assertEqual("test item", self.text())
        self.button("order:" + self.ref)
        self.button("orders:finished:0")
        self.assertIn("pilot-screen:list:Orders:finished:0", self.operations)


if __name__ == "__main__":
    unittest.main()
