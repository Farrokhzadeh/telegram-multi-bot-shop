from decimal import Decimal
from pathlib import Path
import json
import os
import re
import stat
import time
import uuid


def private_text(path, limit):
	with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW), "rb") as stream:
		info = os.fstat(stream.fileno())
		if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077 or not 0 < info.st_size <= limit:
			raise ValueError("private pilot file rejected")
		return stream.read(limit + 1).decode("utf-8")


def request(root, command):
	request_id = uuid.uuid4().hex
	pending = root / (request_id + ".pending")
	with os.fdopen(os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as stream:
		stream.write(command)
	os.replace(pending, root / "requests" / (request_id + ".request"))
	result = root / "results" / (request_id + ".json")
	deadline = time.monotonic() + 8
	while time.monotonic() < deadline:
		try:
			response = json.loads(private_text(result, 4096), parse_float=Decimal)
			if not isinstance(response, dict) or not isinstance(response.get("outcome"), str):
				raise ValueError("pilot response rejected")
			return response
		except FileNotFoundError:
			time.sleep(0.1)
	raise TimeoutError("pilot response expired")


def buttons(*choices):
	return {"inline_keyboard": [[{"text": label, "callback_data": "t079:" + value} for label, value in choices]]}


class WalletPilot:
	def __init__(self, actor, call, checkout):
		if type(actor) is not int or actor <= 0:
			raise ValueError("pilot actor rejected")
		self.actor = actor
		self.call = call
		self.checkout = checkout
		self.anchor = None
		self.completed = False
		self.screen = None
		self.quote = None
		self.quantities = ()

	def authorized(self, message, actor):
		chat = message.get("chat", {})
		return actor.get("id") == self.actor and chat.get("id") == self.actor and chat.get("type") == "private"

	def update(self, event):
		message = event.get("message")
		if message:
			if not self.authorized(message, message.get("from", {})):
				return
			command = message.get("text", "").split(maxsplit=1)
			if not command or command[0] not in ("/start", "/shop", "/shop@testadbadbot", "/checkoutpilot", "/checkoutpilot@testadbadbot"):
				return
			if self.screen == "confirm":
				return
			item = self.checkout("pilot-catalog")
			if item["outcome"] != "Available":
				return
			self.quantities = tuple(n for n in (1, 2) if item["minimum"] <= n <= item["maximum"])
			if not self.quantities:
				return
			sent = self.call("sendMessage", chat_id=self.actor, text="Staging wallet pilot\n" + item["title"],
				reply_markup=buttons(("Select product", "item")), protect_content=True)
			self.anchor, self.screen = sent["message_id"], "catalog"
			return
		query = event.get("callback_query") or {}
		message = query.get("message") or {}
		if self.anchor is None or not self.authorized(message, query.get("from", {})) or self.anchor != message.get("message_id"):
			return
		data = query.get("data", "")
		if not data.startswith("t079:"):
			return
		self.call("answerCallbackQuery", callback_query_id=query["id"])
		choice = data[5:]
		if choice == "item" and self.screen == "catalog":
			self.edit("Choose quantity", buttons(*((str(n), str(n)) for n in self.quantities)))
			self.screen = "quantity"
		elif self.screen == "quantity" and choice in tuple(str(n) for n in self.quantities):
			quote = self.checkout("pilot-quote:" + choice)
			if quote["outcome"] != "Quoted" or not re.fullmatch(r"[0-9a-f-]{36}", quote.get("order") or ""):
				self.edit("Quote unavailable. Open /checkoutpilot to try again.", buttons())
				self.screen = "closed"
				return
			self.quote = uuid.UUID(quote["order"]).hex
			self.edit(f"Quantity: {quote['quantity']}\nTotal: {quote['amount']} {quote['currency'].upper()}",
				buttons(("Pay from test wallet", "pay"), ("Cancel", "cancel")))
			self.screen = "confirm"
		elif choice == "cancel" and self.screen == "confirm":
			self.edit("Checkout cancelled.", buttons())
			self.screen = "closed"
		elif choice == "pay" and self.screen == "confirm":
			accepted = self.checkout("pilot-buy:" + self.quote)
			if accepted["outcome"] in ("Purchased", "Replayed"):
				self.show_receipt(accepted["order"])
				self.completed = True
			else:
				self.edit("Checkout unavailable. Open /checkoutpilot to try again.", buttons())
				self.screen = "closed"

	def show_receipt(self, order):
		self.edit("Test wallet checkout complete.\nOrder: " + order, buttons())
		self.screen = "closed"

	def edit(self, text, reply_markup):
		self.call("editMessageText", chat_id=self.actor, message_id=self.anchor,
			text=text, reply_markup=reply_markup)

class WalletShop(WalletPilot):
	"""AccAbad-style navigation for the isolated staging shop."""

	def update(self, event):
		message = event.get("message")
		if message and self.authorized(message, message.get("from", {})):
			command = (message.get("text") or "").strip()
			commands = {"/start": "home", "/menu": "home", "/wallet": "wallet", "/orders": "orders"}
			name = command.removesuffix("@testadbadbot")
			if name in commands:
				self.show(commands[name], new_message=True)
				return

		query = event.get("callback_query") or {}
		message = query.get("message") or {}
		if self.anchor is not None and self.authorized(message, query.get("from", {})) and message.get("message_id") == self.anchor:
			data = query.get("data", "")
			if data in ("t079:home", "t079:catalog", "t079:wallet", "t079:orders"):
				self.call("answerCallbackQuery", callback_query_id=query["id"])
				self.show(data[5:])
				return
			if data.startswith("t079:reveal:") and self.screen in ("orders", "receipt", "items"):
				order = data[12:]
				if re.fullmatch(r"[0-9a-f]{32}", order):
					self.call("answerCallbackQuery", callback_query_id=query["id"])
					self.show_items(order)
					return
		super().update(event)

	def show(self, page, new_message=False):
		home = [("Main menu", "home")]
		if page == "home":
			text = "Akanti (آکانتی) staging shop"
			rows = [[("Shop", "catalog")], [("Orders", "orders"), ("Wallet", "wallet")]]
		elif page == "catalog":
			item = self.checkout("pilot-catalog")
			text = "Shop"
			rows = []
			if item["outcome"] == "Available":
				self.quantities = tuple(n for n in (1, 2) if item["minimum"] <= n <= item["maximum"])
				text += "\n" + item["title"]
				if self.quantities:
					rows.append([("Select product", "item")])
			else:
				text += "\nThe test product is out of stock."
			rows.append(home)
		elif page == "wallet":
			wallet = self.checkout("pilot-wallet")
			text = "Test wallet\nBalance: " + str(wallet["balance"]) + " USD"
			rows = [home]
		elif page == "orders":
			orders = self.checkout("pilot-orders")["orders"]
			text = "Orders" if orders else "Orders\nNo orders yet."
			rows = [[(f"{o['title'][:34]} × {o['quantity']} · {o['id'][-8:]}", "reveal:" + uuid.UUID(o["id"]).hex)] for o in orders]
			rows.append(home)
		else:
			raise ValueError("Unknown shop screen")
		self.render(text, rows, new_message)
		self.screen = page
		self.quote = None

	def show_receipt(self, order):
		prepared = self.checkout("manifest-worker")
		rows = [[("Orders", "orders"), ("Main menu", "home")]]
		if prepared["outcome"] in ("Prepared", "PreparedWithoutSend"):
			rows.insert(0, [("View item", "reveal:" + uuid.UUID(order).hex)])
		self.render("Test wallet checkout complete.\nOrder: " + order, rows)
		self.screen = "receipt"

	def show_items(self, order):
		released = self.checkout("pilot-reveal:" + order)
		items = released.get("items") if released["outcome"] == "Items" else None
		text = "Test delivery\n" + "\n".join(items) if items else "The item is awaiting delivery or review."
		self.render(text, [[("Orders", "orders"), ("Main menu", "home")]])
		self.screen = "items"

	def render(self, text, rows, new_message=False):
		markup = {"inline_keyboard": [buttons(*row)["inline_keyboard"][0] for row in rows]}
		if new_message:
			message = self.call("sendMessage", chat_id=self.actor, text=text, reply_markup=markup, protect_content=True)
			self.anchor = message["message_id"]
		else:
			self.edit(text, markup)

