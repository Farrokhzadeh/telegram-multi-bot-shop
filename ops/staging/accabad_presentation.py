"""AccAbad customer presentation for the wallet-backed staging storefront.

Copy and template keys come from AccAbad revision 66371a2. Data and actions
come from Akanti. Values inserted into Telegram markup are escaped.
"""
from dataclasses import dataclass
from html import escape, unescape
from html.parser import HTMLParser
import re

from telegram_transport import TelegramError

TEXT = {'en': {'home_dashboard': '<h3><tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji> {store}</h3><p>Welcome, '
                          '<b>{name}</b> 👋</p>{wallet}{rate}{activity}{account}{notices}<p>Choose an option below.</p>',
        'ui_wallet': '<tg-emoji emoji-id="6084583651738132915">👛</tg-emoji> Wallet',
        'ui_activity': '<tg-emoji emoji-id="5413718040799899278">📊</tg-emoji> Purchases',
        'ui_account': '<tg-emoji emoji-id="6084717869466133091">👤</tg-emoji> Account',
        'ui_label': 'Details',
        'ui_value': 'Value',
        'ui_currency': 'Currency',
        'ui_balance': 'Balance',
        'ui_username': 'Username',
        'ui_id': 'Account ID',
        'ui_joined': 'Member since',
        'ui_orders': 'Orders',
        'ui_spent_usdt': 'Spent in USDT',
        'ui_quantity': 'Choose quantity:',
        'ui_wallet_available': 'Available wallet: {0} USDT{1}',
        'ui_support_choose': 'Select a topic so support can help with the right information.',
        'ui_support_order': 'Order problem',
        'ui_support_payment': 'Payment problem',
        'ui_buying_help': 'Buying help',
        'ui_buying_guide': 'Browse the store and open a product for its description, prices and rules. Choose your '
                           'quantity, confirm the total, then pay from your wallet or use an available direct payment '
                           'method. Purchased credentials and usage guides appear in My Orders. Service orders show '
                           'their progress there.',
        'ui_reveal': '<tg-emoji emoji-id="5445095448366823628">🔑</tg-emoji> Show credentials',
        'ui_all': 'All',
        'ui_active': 'Active',
        'ui_finished': 'Finished',
        'ui_deposits': '<tg-emoji emoji-id="6082631060821122101">🧾</tg-emoji> Deposit requests',
        'ui_page': 'Page {0} / {1}',
        'ui_pay_usdt': '<tg-emoji emoji-id="6084583651738132915">💳</tg-emoji> Pay {0} USDT from wallet',
        'b_shop': '<tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji> BROWSE SHOP',
        'b_orders': '<tg-emoji emoji-id="6082631060821122101">📦</tg-emoji> My Orders',
        'b_wallet': '<tg-emoji emoji-id="6084583651738132915">🏦</tg-emoji> My Wallet',
        'b_my_discounts': '<tg-emoji emoji-id="6039495948353146588">🎟️</tg-emoji> My Discounts',
        'b_sup': '<tg-emoji emoji-id="6084561249188714482">🎧</tg-emoji> Support Desk',
        'b_lang': '<tg-emoji emoji-id="6084528951034648578">🌐</tg-emoji> Change Language',
        'b_home': '<tg-emoji emoji-id="5321429707688801442">🏠</tg-emoji> Return Home',
        'b_back': '<tg-emoji emoji-id="5807737309742763644">↩️</tg-emoji> Back',
        'b_cont': '<tg-emoji emoji-id="6084561249188714482">💬</tg-emoji> Contact {0}',
        'b_cqty': 'Custom quantity',
        'b_cancel': '<tg-emoji emoji-id="6082643408852099504">❌</tg-emoji> Cancel',
        'b_next': '<tg-emoji emoji-id="6082234751303817560">🔄</tg-emoji> Next',
        'b_prev': 'Prev',
        'b_tx': '<tg-emoji emoji-id="6082165615215254878">📒</tg-emoji> View Transaction History',
        'buy_one': '<tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji> Buy 1 item',
        'buy_more': '<tg-emoji emoji-id="6084836861535066784">🔢</tg-emoji> More?',
        'store_title': '<tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji> Store · {0} products',
        'store_empty': '<tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji> <b>Store</b>\nNo products available yet.',
        'store_refresh': '<tg-emoji emoji-id="6084846593930959393">🔄</tg-emoji> Refresh',
        'store_table': '<h3>{title}</h3>{table}<p>{discount_notice}</p><p>{page}</p>',
        'store_product': 'Product',
        'store_price_usdt': 'USDT',
        'store_availability': 'Availability',
        'store_available': 'Available',
        'store_stock_count': '{0} items',
        'p_det': '<blockquote><tg-emoji emoji-id="5411307602074163455">🏷️</tg-emoji> <b>{0}</b> </blockquote>\n'
                 '<i>Price (USD):</i> <b>${1:.2f} / unit</b>\n'
                 '{2}\n'
                 '-----------------------\n'
                 '<tg-emoji emoji-id="5323289282499064033">📦</tg-emoji> <i>Current Stock:</i> <b>{3}</b>\n'
                 '-----------------------\n'
                 '\n'
                 '<tg-emoji emoji-id="6084555554062080495">📝</tg-emoji> <b>Description:</b>\n'
                 '{4}\n'
                 '\n'
                 '<b>Next step:</b> Choose 1, 2, 3, 5, or 10 below. For another amount, tap <b>Custom quantity</b>.',
        'p_out_stock': '<blockquote><tg-emoji emoji-id="5411307602074163455">🏷️</tg-emoji> <b>{0}</b> </blockquote>\n'
                       '-----------------------\n'
                       '<tg-emoji emoji-id="6084842505122094054">⚠️</tg-emoji> <b>This product is currently out of '
                       'stock.</b>\n'
                       '-----------------------\n'
                       '\n'
                       '<tg-emoji emoji-id="6084555554062080495">📝</tg-emoji> <b>Description:</b>\n'
                       '{1}',
        'prod_out_of_stock': 'Out of stock',
        'discount_auto': 'The best eligible discount is applied automatically at checkout.',
        'pay': '<blockquote><tg-emoji emoji-id="6084583651738132915">💳</tg-emoji> <b>SELECT PAYMENT METHOD</b> '
               '</blockquote>\n'
               '\n'
               'Total for {0}x <b>{1}</b>:\n'
               '<i>Total (USD):</i> <b>${2:.2f}</b>\n'
               '{3}',
        'receipt_compact': '🧾 <b>Purchase receipt</b>\n'
                           '\n'
                           '<b>{product}</b>\n'
                           'Payment confirmed\n'
                           'Quantity: {quantity}\n'
                           'Paid: <b>{total}</b>\n'
                           '\n'
                           'Order: <code>{id}</code>\n'
                           'Date: {date}',
        'ord_mt': '<blockquote><tg-emoji emoji-id="5323289282499064033">📦</tg-emoji> <b>ORDER HISTORY</b> '
                  '</blockquote>\n'
                  '\n'
                  '<i>You haven\'t placed any orders yet. <tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji></i>',
        'tx': '<blockquote><tg-emoji emoji-id="6082165615215254878">📒</tg-emoji> <b>TRANSACTION HISTORY</b> '
              '</blockquote>\n'
              '\n'
              '{0}',
        'tx_mt': '<blockquote><tg-emoji emoji-id="6082165615215254878">📒</tg-emoji> <b>TRANSACTIONS</b> </blockquote>\n'
                 '\n'
                 '<i>No transactions found.</i>',
        'language_en': '<tg-emoji emoji-id="5460804158107969349">🇺🇸</tg-emoji> English',
        'language_fa': '<tg-emoji emoji-id="5461152643164429181">🇮🇷</tg-emoji> فارسی'},
 'fa': {'home_dashboard': '<h3><tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji> {store}</h3><p>خوش آمدید، '
                          '<b>{name}</b> 👋</p>{wallet}{rate}{activity}{account}{notices}<p>یک گزینه را انتخاب '
                          'کنید.</p>',
        'ui_wallet': '<tg-emoji emoji-id="6084583651738132915">👛</tg-emoji> کیف پول',
        'ui_activity': '<tg-emoji emoji-id="5413718040799899278">📊</tg-emoji> سابقه خرید',
        'ui_account': '<tg-emoji emoji-id="6084717869466133091">👤</tg-emoji> اطلاعات حساب',
        'ui_label': 'مشخصات',
        'ui_value': 'مقدار',
        'ui_currency': 'ارز',
        'ui_balance': 'موجودی',
        'ui_username': 'نام کاربری',
        'ui_id': 'شناسه حساب',
        'ui_joined': 'تاریخ عضویت',
        'ui_orders': 'تعداد سفارش\u200cها',
        'ui_spent_usdt': 'مجموع خرید با تتر',
        'ui_quantity': 'تعداد را انتخاب کنید:',
        'ui_wallet_available': 'موجودی کیف پول: {0} تتر{1}',
        'ui_support_choose': 'موضوع را انتخاب کنید تا پشتیبانی با اطلاعات درست کمک کند.',
        'ui_support_order': 'مشکل سفارش',
        'ui_support_payment': 'مشکل پرداخت',
        'ui_buying_help': 'راهنمای خرید',
        'ui_buying_guide': 'فروشگاه را باز کنید و توضیحات، قیمت و قوانین محصول را بخوانید. تعداد را انتخاب کنید، مبلغ '
                           'نهایی را تأیید کنید و از کیف پول یا روش پرداخت مستقیم موجود استفاده کنید. اطلاعات خرید و '
                           'راهنمای استفاده در سفارش\u200cهای من قرار می\u200cگیرد. روند انجام خدمات هم از '
                           'همان\u200cجا قابل پیگیری است.',
        'ui_reveal': '<tg-emoji emoji-id="5445095448366823628">🔑</tg-emoji> نمایش اطلاعات خرید',
        'ui_all': 'همه',
        'ui_active': 'فعال',
        'ui_finished': 'پایان\u200cیافته',
        'ui_deposits': '<tg-emoji emoji-id="6082631060821122101">🧾</tg-emoji> درخواست\u200cهای واریز',
        'ui_page': 'صفحه {0} از {1}',
        'ui_pay_usdt': '<tg-emoji emoji-id="6084583651738132915">💳</tg-emoji> پرداخت {0} تتر از کیف پول',
        'b_shop': '<tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji> مشاهده فروشگاه',
        'b_orders': '<tg-emoji emoji-id="6082631060821122101">📦</tg-emoji> سفارشات من',
        'b_wallet': '<tg-emoji emoji-id="6084583651738132915">🏦</tg-emoji> کیف پول من',
        'b_my_discounts': '<tg-emoji emoji-id="6039495948353146588">🎟️</tg-emoji> تخفیف\u200cهای من',
        'b_sup': '<tg-emoji emoji-id="6084561249188714482">🎧</tg-emoji> میز پشتیبانی',
        'b_lang': '<tg-emoji emoji-id="6084528951034648578">🌐</tg-emoji> تغییر زبان',
        'b_home': '<tg-emoji emoji-id="5321429707688801442">🏠</tg-emoji> بازگشت به خانه',
        'b_back': '<tg-emoji emoji-id="5807737309742763644">↩️</tg-emoji> بازگشت',
        'b_cont': '<tg-emoji emoji-id="6084561249188714482">💬</tg-emoji> ارتباط با {0}',
        'b_cqty': 'تعداد دلخواه',
        'b_cancel': '<tg-emoji emoji-id="6082643408852099504">❌</tg-emoji> لغو',
        'b_next': '<tg-emoji emoji-id="6082234751303817560">🔄</tg-emoji> بعدی',
        'b_prev': 'قبلی',
        'b_tx': '<tg-emoji emoji-id="6082165615215254878">📒</tg-emoji> مشاهده تاریخچه تراکنش\u200cها',
        'buy_one': '<tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji> خرید ۱ عدد',
        'buy_more': '<tg-emoji emoji-id="6084836861535066784">🔢</tg-emoji> تعداد بیشتر؟',
        'store_title': '<tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji> فروشگاه · {0} محصول',
        'store_empty': '<tg-emoji emoji-id="6082290027532918112">🛒</tg-emoji> <b>فروشگاه</b>\n'
                       'در حال حاضر محصولی موجود نیست.',
        'store_refresh': '<tg-emoji emoji-id="6084846593930959393">🔄</tg-emoji> بروزرسانی',
        'store_table': '<h3>{title}</h3>{table}<p>{discount_notice}</p><p>{page}</p>',
        'store_product': 'محصول',
        'store_price_usdt': 'USDT',
        'store_availability': 'موجودی',
        'store_available': 'موجود',
        'store_stock_count': '{0} عدد',
        'p_det': '<blockquote><tg-emoji emoji-id="5411307602074163455">🏷️</tg-emoji> <b>{0}</b> </blockquote>\n'
                 '<i>قیمت (دلار):</i> <b>${1:.2f} / عدد</b>\n'
                 '{2}\n'
                 '-----------------------\n'
                 '<tg-emoji emoji-id="5323289282499064033">📦</tg-emoji> <i>موجودی فعلی:</i> <b>{3}</b>\n'
                 '-----------------------\n'
                 '\n'
                 '<tg-emoji emoji-id="6084555554062080495">📝</tg-emoji> <b>توضیحات:</b>\n'
                 '{4}\n'
                 '\n'
                 '<b>مرحله بعد:</b> تعداد 1، 2، 3، 5 یا 10 را انتخاب کنید. برای تعداد دیگر، روی <b>تعداد دلخواه</b> '
                 'بزنید.',
        'p_out_stock': '<blockquote><tg-emoji emoji-id="5411307602074163455">🏷️</tg-emoji> <b>{0}</b> </blockquote>\n'
                       '-----------------------\n'
                       '<tg-emoji emoji-id="6084842505122094054">⚠️</tg-emoji> <b>این محصول فعلاً ناموجود است.</b>\n'
                       '-----------------------\n'
                       '\n'
                       '<tg-emoji emoji-id="6084555554062080495">📝</tg-emoji> <b>توضیحات:</b>\n'
                       '{1}',
        'prod_out_of_stock': 'ناموجود',
        'discount_auto': 'بهترین تخفیف قابل استفاده، هنگام خرید به\u200cصورت خودکار اعمال می\u200cشود.',
        'pay': '<blockquote><tg-emoji emoji-id="6084583651738132915">💳</tg-emoji> <b>انتخاب روش پرداخت</b> '
               '</blockquote>\n'
               '\n'
               'مجموع برای {0} عدد <b>{1}</b>:\n'
               '<i>مبلغ کل (دلار):</i> <b>${2:.2f}</b>\n'
               '{3}',
        'receipt_compact': '🧾 <b>رسید خرید</b>\n'
                           '\n'
                           '<b>{product}</b>\n'
                           'پرداخت تأیید شده\n'
                           'تعداد: {quantity}\n'
                           'مبلغ پرداخت\u200cشده: <b>{total}</b>\n'
                           '\n'
                           'شناسه سفارش: <code>{id}</code>\n'
                           'تاریخ: {date}',
        'ord_mt': '<blockquote><tg-emoji emoji-id="5323289282499064033">📦</tg-emoji> <b>تاریخچه سفارشات</b> '
                  '</blockquote>\n'
                  '\n'
                  '<i>شما هنوز هیچ سفارشی ثبت نکرده\u200cاید. <tg-emoji '
                  'emoji-id="6082290027532918112">🛒</tg-emoji></i>',
        'tx': '<blockquote><tg-emoji emoji-id="6082165615215254878">📒</tg-emoji> <b>تاریخچه تراکنش\u200cها</b> '
              '</blockquote>\n'
              '\n'
              '{0}',
        'tx_mt': '<blockquote><tg-emoji emoji-id="6082165615215254878">📒</tg-emoji> <b>تراکنش\u200cها</b> '
                 '</blockquote>\n'
                 '\n'
                 '<i>هیچ تراکنشی یافت نشد.</i>',
        'language_en': '<tg-emoji emoji-id="5460804158107969349">🇺🇸</tg-emoji> English',
        'language_fa': '<tg-emoji emoji-id="5461152643164429181">🇮🇷</tg-emoji> فارسی'}}


def copy(language, key, *args, **fields):
    return TEXT[language][key].format(*args, **fields)


emoji = re.compile(r'<tg-emoji emoji-id="([0-9]+)">(.*?)</tg-emoji>', re.S)
tags = re.compile(r"<[^>]*>")
icons = {match[1]: match[2] for language in TEXT.values() for value in language.values()
         for match in emoji.finditer(value)}


def button(caption, action, style=None):
    match = emoji.search(caption)
    if match:
        caption = caption[:match.start()] + caption[match.end():]
    result = {"text": unescape(tags.sub("", caption)).strip()[:64], "callback_data": "t079:" + action}
    if match:
        result["icon_custom_emoji_id"] = match[1]
    if style:
        result["style"] = style
    return result


def table(headers, rows):
    head = "".join(f"<th>{escape(str(value))}</th>" for value in headers)
    cells = "".join("<tr>" + "".join(f"<td>{value}</td>" for value in row) + "</tr>" for row in rows)
    return f"<table bordered striped><tr>{head}</tr>{cells}</table>"


def dollars(amount):
    return f"{amount:,.2f}"


@dataclass(frozen=True)
class Screen:
    html: str


def home(language, profile, actor, name, username):
    s = lambda key: copy(language, key)
    wallet = f"<h4>{s('ui_wallet')}</h4>" + table((s("ui_currency"), s("ui_balance")),
                                                  [("USDT", dollars(profile["balance"]))])
    activity = f"<h4>{s('ui_activity')}</h4>" + table((s("ui_label"), s("ui_value")),
        [(s("ui_orders"), str(profile["orders"])), (s("ui_spent_usdt"), dollars(profile.get("spent", 0)))])
    account = f"<h4>{s('ui_account')}</h4>" + table((s("ui_label"), s("ui_value")),
        [(s("ui_username"), escape("@" + username) if username else "—"),
         (s("ui_id"), f"<code>{actor}</code>"), (s("ui_joined"), escape(profile["joined"][:10]))])
    return Screen(copy(language, "home_dashboard", store="Akanti (آکانتی)", name=escape(name or str(actor)),
                       wallet=wallet, rate="", activity=activity, account=account, notices=""))


def wallet(language, balance):
    s = lambda key: copy(language, key)
    return Screen(f"<h3>{s('ui_wallet')}</h3>" + table((s("ui_currency"), s("ui_balance")), [("USDT", dollars(balance))]))


def catalog(language, item):
    if not item.get("title"):
        return Screen(copy(language, "store_empty"))
    ready = item["outcome"] == "Available"
    status = copy(language, "store_available") if ready else copy(language, "prod_out_of_stock")
    if ready and item.get("stock") is not None:
        status += " · " + copy(language, "store_stock_count", item["stock"])
    columns = [copy(language, key) for key in ("store_product", "store_price_usdt", "store_availability")]
    contents = table(columns, [(escape(item["title"]), dollars(item["amount"]), status)])
    return Screen(copy(language, "store_table", title=copy(language, "store_title", 1), table=contents,
                       discount_notice="", page=copy(language, "ui_page", 1, 1)))


class TextPresentation(HTMLParser):
    supported = {"b", "strong", "i", "em", "s", "code", "pre", "blockquote"}
    blocks = {"p", "br", "tr", "table", "h3", "h4"}

    def __init__(self, html):
        super().__init__()
        self.parts = []
        self.feed(html)

    def handle_starttag(self, tag, attrs):
        if tag in self.supported:
            self.parts.append(f"<{tag}>")
        elif tag in self.blocks:
            self.parts.append("\n")
            if tag in ("h3", "h4"):
                self.parts.append("<b>")
        elif tag in ("th", "td") and self.parts and not self.parts[-1].endswith("\n"):
            self.parts.append(" | ")

    def handle_endtag(self, tag):
        if tag in self.supported:
            self.parts.append(f"</{tag}>")
        elif tag in ("h3", "h4"):
            self.parts.append("</b>\n")
        elif tag in self.blocks:
            self.parts.append("\n")

    def handle_data(self, data):
        self.parts.append(escape(data))

    def text(self):
        return re.sub(r"\n\s*\n+", "\n\n", "".join(self.parts)).strip()


def present(call, chat, message_id, screen, markup, rtl):
    params = {"chat_id": chat, "reply_markup": markup}
    if message_id is None:
        params["protect_content"] = True
    else:
        params["message_id"] = message_id
    ordinary = "sendMessage" if message_id is None else "editMessageText"
    if not isinstance(screen, Screen):
        return call(ordinary, text=screen, **params)
    rich = "sendRichMessage" if message_id is None else ordinary
    try:
        return call(rich, rich_message={"html": screen.html, "is_rtl": rtl}, **params)
    except TelegramError as error:
        if error.code not in (400, 404):
            raise
        retry_without_custom_emoji = error.code == 400
    plain_markup = {"inline_keyboard": []}
    for row in markup["inline_keyboard"]:
        plain_row = []
        for item in row:
            rendered = dict(item)
            icon = rendered.pop("icon_custom_emoji_id", None)
            if icon in icons:
                rendered["text"] = f"{icons[icon]} {rendered['text']}"[:64]
            plain_row.append(rendered)
        plain_markup["inline_keyboard"].append(plain_row)
    params["reply_markup"] = plain_markup
    clean_html = emoji.sub(r"\2", screen.html)
    if retry_without_custom_emoji and (clean_html != screen.html or plain_markup != markup):
        try:
            return call(rich, rich_message={"html": clean_html, "is_rtl": rtl}, **params)
        except TelegramError as error:
            if error.code not in (400, 404):
                raise
    return call(ordinary, text=TextPresentation(screen.html).text(), parse_mode="HTML", **params)
