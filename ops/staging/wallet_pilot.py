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
			response = json.loads(private_text(result, 16384), parse_float=Decimal)
			if not isinstance(response, dict) or not isinstance(response.get("outcome"), str):
				raise ValueError("pilot response rejected")
			if command.startswith("pilot-screen:"):
				result.unlink(missing_ok=True)
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
	"""Customer screens from the AccAbad flow, backed by Akanti staging."""

	labels = {
		"home": ("Main menu", "منوی اصلی"), "catalog": ("Shop", "فروشگاه"),
		"wallet": ("Wallet", "کیف پول"), "orders": ("Orders", "سفارش‌ها"),
		"discounts": ("My discounts", "تخفیف‌های من"), "support": ("Support", "پشتیبانی"),
		"language": ("Language", "زبان"), "balance": ("Balance", "موجودی"),
		"joined": ("Joined", "عضویت"), "back": ("Back", "بازگشت"),
		"next": ("Next", "بعدی"), "previous": ("Previous", "قبلی"),
		"all": ("All", "همه"), "active": ("Active", "فعال"), "finished": ("Finished", "پایان‌یافته"),
		"transactions": ("Transactions", "تراکنش‌ها"), "deposits": ("Payments", "پرداخت‌ها"),
		"buying_help": ("Buying guide", "راهنمای خرید"), "support_orders": ("Order support", "پشتیبانی سفارش"),
		"support_payments": ("Payment support", "پشتیبانی پرداخت"),
		"empty": ("No records found.", "موردی پیدا نشد."), "out": ("Out of stock", "ناموجود"),
		"buy": ("Buy", "خرید"), "quantity": ("Quantity", "تعداد"),
		"quantity_help": ("Choose or enter a quantity.", "تعداد را انتخاب یا وارد کنید."),
		"total": ("Total", "مبلغ کل"), "pay": ("Pay from test wallet", "پرداخت از کیف پول آزمایشی"),
		"cancel": ("Cancel", "لغو"), "paid": ("Test wallet checkout complete.", "خرید آزمایشی انجام شد."),
		"unavailable": ("Unavailable. Please try again from the shop.", "در دسترس نیست. از فروشگاه دوباره تلاش کنید."),
		"items": ("View purchased item", "نمایش خرید"), "awaiting": ("Awaiting delivery or review.", "در انتظار تحویل یا بررسی."),
		"status": ("Status", "وضعیت"), "amount": ("Amount", "مبلغ"),
		"date": ("Date", "تاریخ"), "member": ("Telegram ID", "شناسه تلگرام"),
		"help_text": ("Open Shop, choose a product and quantity, then confirm checkout. Find the order and item under Orders.",
					  "فروشگاه را باز کنید، محصول و تعداد را انتخاب کنید و خرید را تأیید کنید. سفارش و محصول در بخش سفارش‌ها هستند."),
		"ready": ("Ready", "آماده"), "completed": ("Completed", "تکمیل شده"),
		"waiting_stock": ("Waiting for stock", "در انتظار موجودی"), "requires_admin": ("Needs review", "نیازمند بررسی"),
		"purchase_debit": ("Purchase", "خرید"), "admin_adjustment": ("Wallet adjustment", "اصلاح موجودی"),
		"opening_balance": ("Opening balance", "موجودی اولیه"), "wallet_topup": ("Top-up", "افزایش موجودی"),
		"settled": ("Settled", "تسویه‌شده"), "review": ("In review", "در حال بررسی"),
		"awaiting_payment": ("Awaiting payment", "در انتظار پرداخت"), "expired": ("Expired", "منقضی"),
		"cancelled": ("Cancelled", "لغوشده"), "bank": ("Bank transfer", "انتقال بانکی"), "crypto": ("Crypto", "رمزارز"),
	}

	def __init__(self, actor, call, checkout):
		super().__init__(actor, call, checkout)
		self.language = "en"
		self.list_origin = "orders:all:0"

	def label(self, key):
		return self.labels.get(key, (key.replace("_", " ").capitalize(),) * 2)[self.language == "fa"]

	def update(self, event):
		message = event.get("message")
		if message:
			if not self.authorized(message, message.get("from", {})):
				return
			words = (message.get("text") or "").strip().lower().split(maxsplit=1)
			if not words:
				return
			command = words[0].removesuffix("@testadbadbot")
			pages = {"/start": "home", "/menu": "home", "/profile": "home", "/help": "home", "/shop": "catalog",
					 "/checkoutpilot": "catalog", "/orders": "orders:all:0", "/wallet": "wallet", "/support": "support",
					 "/cancel": "catalog"}
			if command in pages:
				self.open(pages[command], new_message=True)
			elif self.screen == "quantity" and words[0].isdecimal() and len(words[0]) <= 5:
				self.quote_quantity(int(words[0]))
			return

		query = event.get("callback_query") or {}
		message = query.get("message") or {}
		data = query.get("data", "")
		if self.anchor is None or message.get("message_id") != self.anchor or not data.startswith("t079:") or not self.authorized(message, query.get("from", {})):
			return
		self.call("answerCallbackQuery", callback_query_id=query["id"])
		self.open(data[5:])

	def open(self, destination, new_message=False):
		parts = destination.split(":")
		page = parts[0]
		if new_message or page in ("home", "wallet", "language", "support"):
			profile = self.checkout("pilot-screen:home")["home"]
			self.language = "fa" if profile["language"].startswith("fa") else "en"
		else:
			profile = None
		l = self.label
		home = (l("home"), "home")
		if page == "home":
			text = (f"Akanti (آکانتی)\n{l('balance')}: {profile['balance']} USD"
					f"\n{l('orders')}: {profile['orders']}\n{l('member')}: {self.actor}\n{l('joined')}: {profile['joined'][:10]}")
			rows = [[(l("catalog"), "catalog")], [(l("orders"), "orders:all:0")],
					[(l("wallet"), "wallet"), (l("discounts"), "discounts:0")],
					[(l("support"), "support"), (l("language"), "language")]]
		elif page in ("wallet", "language", "support"):
			text = l(page)
			if page == "wallet":
				text += f"\n{l('balance')}: {profile['balance']} USD"
				rows = [[(l("transactions"), "transactions:0"), (l("deposits"), "deposits:0")], [home]]
			elif page == "language":
				rows = [[("فارسی", "setlang:fa"), ("English", "setlang:en")], [home]]
			else:
				if profile["contact"]:
					text += "\n@" + profile["contact"]
				rows = [[(l("support_orders"), "support_orders:all:0"), (l("support_payments"), "support_payments:0")],
						[(l("buying_help"), "buying_help")], [home]]
		elif page == "setlang" and self.screen == "language" and len(parts) == 2 and parts[1] in ("en", "fa"):
			self.checkout("pilot-screen:language:" + parts[1])
			self.open("home")
			return
		elif page == "buying_help":
			text, rows = l("help_text"), [[(l("back"), "support"), (l("catalog"), "catalog")], [home]]
		elif page in ("catalog", "product", "quantity"):
			item = self.checkout("pilot-catalog")
			text, rows = self.product_screen(page, item)
		elif page in ("orders", "transactions", "deposits", "discounts", "support_orders", "support_payments"):
			text, rows = self.history_screen(page, parts[1:])
		elif page in ("order", "payment", "reveal") and len(parts) == 2 and re.fullmatch(r"[0-9a-f]{32}", parts[1]):
			text, rows = self.detail_screen(page, parts[1])
		elif page == "qty" and self.screen == "quantity" and len(parts) == 2 and parts[1].isascii() and parts[1].isdecimal():
			self.quote_quantity(int(parts[1]))
			return
		elif page == "pay" and self.screen == "confirm" and self.quote:
			accepted = self.checkout("pilot-buy:" + self.quote)
			if accepted["outcome"] in ("Purchased", "Replayed"):
				self.show_receipt(accepted["order"])
				self.completed = True
				return
			text, rows = l("unavailable"), [[(l("catalog"), "catalog"), home]]
		else:
			return
		self.render(text, rows, new_message)
		self.screen = page
		self.quote = None

	def product_screen(self, page, item):
		l = self.label
		back = [(l("back"), "catalog"), (l("home"), "home")]
		if page == "catalog":
			rows = [[(item["title"][:55], "product")]] if item.get("title") else []
			return l("catalog"), rows + [[(l("home"), "home")]]
		if not item.get("title"):
			return l("unavailable"), [back]
		text = item["title"]
		if item.get("description"):
			text += "\n" + item["description"][:1700]
		text += f"\n{l('amount')}: {item['amount']} USD"
		if item["outcome"] != "Available":
			return text + "\n" + l("out"), [back]
		if page == "product":
			return text, [[(l("buy"), "quantity")], back]
		btns = [(str(n), f"qty:{n}") for n in range(item["minimum"], min(item["maximum"], item["minimum"] + 4) + 1)]
		return text + "\n" + l("quantity_help"), [btns, [(l("back"), "product"), (l("home"), "home")]]

	def quote_quantity(self, quantity):
		l = self.label
		if not 1 <= quantity <= 10000:
			return
		quote = self.checkout("pilot-quote:" + str(quantity))
		if quote["outcome"] != "Quoted":
			self.render(l("unavailable"), [[(l("back"), "quantity"), (l("catalog"), "catalog")]])
			return
		self.quote = uuid.UUID(quote["order"]).hex
		self.render(f"{l('quantity')}: {quote['quantity']}\n{l('total')}: {quote['amount']} {quote['currency'].upper()}",
					[[(l("pay"), "pay")], [(l("back"), "quantity"), (l("cancel"), "catalog")]])
		self.screen = "confirm"

	def history_screen(self, destination, args):
		l = self.label
		source = {"support_orders": "orders", "support_payments": "deposits"}.get(destination, destination)
		if len(args) != (2 if source == "orders" else 1):
			raise ValueError("Invalid history page")
		filter_name, raw_page = args if source == "orders" else ("all", args[0])
		if filter_name not in ("all", "active", "finished") or not re.fullmatch(r"[0-9]{1,6}", raw_page):
			raise ValueError("Invalid history page")
		page = int(raw_page)
		data = self.checkout(f"pilot-screen:list:{source.title()}:{filter_name}:{page}")["page"]
		rows = []
		if source == "orders":
			rows.append([(f"✓ {l(name)}" if name == filter_name else l(name), f"{destination}:{name}:0")
						 for name in ("all", "active", "finished")])
		text = l(destination)
		entries = data["entries"]
		if not entries:
			text += "\n" + l("empty")
		for entry in entries:
			reference = uuid.UUID(entry["id"]).hex
			if source == "orders":
				rows.append([(f"{entry['title'][:34]} × {entry['quantity']} · {reference[-8:]}", "order:" + reference)])
			elif source == "deposits":
				rows.append([(f"{l(entry['title'])} · {entry['amount']} {entry['currency']} · {l(entry['status'])}", "payment:" + reference)])
			else:
				title = l(entry["title"]) if source == "transactions" else entry["title"]
				text += f"\n{entry['created'][:10]} · {title[:60]} · {entry['amount']} {entry['currency']}"
		prefix = f"{destination}:{filter_name}:" if source == "orders" else f"{destination}:"
		pagination = []
		if page > 0:
			pagination.append((l("previous"), prefix + str(page - 1)))
		if data["hasNext"]:
			pagination.append((l("next"), prefix + str(page + 1)))
		if pagination:
			rows.append(pagination)
		back = "support" if destination.startswith("support_") else "wallet" if source in ("transactions", "deposits") else "home"
		home = (l("home"), "home")
		rows.append([home] if back == "home" else [(l("back"), back), home])
		self.list_origin = prefix + str(page)
		return text, rows

	def detail_screen(self, page, reference):
		l = self.label
		list_type = "Deposits" if page == "payment" else "Orders"
		if page == "reveal":
			result = self.checkout("pilot-reveal:" + reference)
			rows = [[(l("back"), "order:" + reference), (l("home"), "home")]]
			return "\n".join(result["items"]) if result["outcome"] == "Items" else l("awaiting"), rows
		entry = self.checkout(f"pilot-screen:detail:{list_type}:{reference}").get("entry")
		rows = [[(l("back"), self.list_origin), (l("home"), "home")]]
		if not entry:
			return l("empty"), rows
		title = l(entry["title"]) if page == "payment" else entry["title"]
		text = f"{title}\n{entry['id']}\n{l('status')}: {l(entry['status'])}\n{l('date')}: {entry['created'][:10]}"
		if entry.get("quantity"):
			text += f"\n{l('quantity')}: {entry['quantity']}"
		if entry.get("amount") is not None:
			text += f"\n{l('amount')}: {entry['amount']} {entry['currency']}"
		if page == "order" and entry["status"] in ("ready", "delivering", "completed"):
			rows.insert(0, [(l("items"), "reveal:" + reference)])
		return text, rows

	def show_receipt(self, order):
		self.checkout("manifest-worker")
		l = self.label
		self.list_origin = "orders:all:0"
		self.render(f"{l('paid')}\n{order}", [[(l("orders"), "order:" + uuid.UUID(order).hex)], [(l("home"), "home")]])
		self.screen = "receipt"

	def render(self, text, rows, new_message=False):
		markup = {"inline_keyboard": [buttons(*row)["inline_keyboard"][0] for row in rows]}
		if new_message:
			message = self.call("sendMessage", chat_id=self.actor, text=text, reply_markup=markup, protect_content=True)
			self.anchor = message["message_id"]
		else:
			self.edit(text, markup)
