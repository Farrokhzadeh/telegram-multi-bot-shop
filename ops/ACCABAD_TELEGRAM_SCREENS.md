# AccAbad Telegram screen inventory

The source reference is [AccAbad revision 66371a2](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/management/commands/runbot.py). The runtime entry point registers `/start`, `/menu`, `/shop`, `/profile`, `/orders`, `/support`, `/help`, `/cancel`, `/admin`, `/panel`, `/user`, and `/emoji`, plus text, photo, video, document, and button handlers. The table covers full workflows, including screens rendered directly by a handler.

| AccAbad flow | Akanti coverage | Source |
| --- | --- | --- |
| Home, profile, language, main navigation and cancel | Staging reads the member summary and persists English or Persian. | [Commands](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/handlers.py#L553), [home and language](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L201) |
| Catalog, product detail, purchase quantity, quote, wallet confirmation and receipt | Staging uses the existing catalog, quote, wallet checkout and manifest services with the test catalog. Legacy rules acceptance and non-wallet checkout still need a runtime. | [Catalog and checkout](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L927), [shop flow](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/shop_flows.py) |
| Wallet, transactions, payment list and payment details | Staging reads the wallet ledger and payments scoped to the verified member. Funding forms, receipt upload, payment resume and live payment callbacks still need wiring. | [Wallet and payments](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py#L250), [deposit UI](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/deposit_ui.py) |
| Orders, filters, paging, order detail and purchased item | Staging uses the member's orders, saved product details, wallet postings and authorized test-item retrieval. Automatic Telegram dispatch, file delivery, resend and service-order input are still open. | [Order screens](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py#L35), [purchase delivery UI](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_purchase_ui.py) |
| My discounts and offer detail | Staging lists active personal offers. Offer detail, redeem and prepaid-code flows still need UI wiring. | [Discount callbacks](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/handlers.py#L2537), [discount screens](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L653) |
| Support menu, buying help, order and payment support | Staging exposes the buying guide, member-scoped orders and payments, and the verified public contact when configured. Ticket replies, staff escalation and Business AI still need transport integration. | [Support screens](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1409), [support callbacks](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py#L418) |
| Membership prompt and channel recheck | Pending. The backend has channel policy and checkout eligibility. | [Force join menu](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L363), [verify callback](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/handlers.py#L1419) |
| Admin dashboard, navigation, orders, detail, review and recovery | Pending. Operator API operations exist. Telegram staff sessions and screens still need wiring. | [Admin flows](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py), [staff navigation](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_navigation.py) |
| Admin products, type, editing, removal, duplication, stock actions and import | Pending. The catalog and inventory APIs are the data path for these screens. | [Catalog actions](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py#L359), [inventory callbacks](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/handlers.py#L2250) |
| Admin users, search, activity, finance, wallet changes, bans and discounts | Pending. Needs authenticated staff routing and operator flows. | [User and finance callbacks](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/handlers.py#L2335), [staff user screens](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1110) |
| Admin receipt review, approval, rejection reason and confirmation | Pending. The backend has receipt administration. Telegram media and review screens still need wiring. | [Receipt actions](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/handlers.py#L2127), [review UI](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/deposit_ui.py#L142) |
| Admin settings, staff roles, channels, notifications, stats and thresholds | Pending. These settings and actions need operator screens. | [Settings and reports](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py#L454), [admin keyboards](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L371) |
| Admin messages, broadcast preview, test, confirmation and reports | Pending. Requires staff authorization, content capture, and campaign worker wiring. | [Broadcast callbacks](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/handlers.py#L1430), [broadcast screens](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L411) |
| Admin service jobs, status updates, customer service input and staff media | Pending. Legacy service and staff workflows need explicit equivalents. | [Service callbacks](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/handlers.py#L1790), [service input](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/service_input.py) |

## Named screen builders

These direct source links cover keyboard builders and screen renderers. Callback variants such as individual order IDs, page numbers, and stock actions use the parent screens in the table.

### keyboards.py

- [`get_language_select_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L201)
- [`get_main_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L315)
- [`get_force_join_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L363)
- [`get_admin_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L371)
- [`get_admin_product_type_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L402)
- [`get_admin_messaging_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L411)
- [`get_admin_message_preview_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L423)
- [`get_admin_deposit_review_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L431)
- [`get_admin_deny_reason_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L458)
- [`get_admin_deny_deposit_confirm_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L469)
- [`get_admin_settings_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L476)
- [`get_admin_select_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L488)
- [`get_admin_manage_channels_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L497)
- [`get_admin_edit_prod_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L508)
- [`get_admin_ep_fields_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L521)
- [`get_remove_product_confirm_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L545)
- [`get_admin_mng_inv_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L552)
- [`get_admin_inv_action_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L562)
- [`get_admin_discounts_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L622)
- [`get_user_discounts_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L653)
- [`get_user_discount_detail_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L685)
- [`get_admin_manage_disc_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L720)
- [`get_admin_stats_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L748)
- [`get_admin_deposits_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L756)
- [`get_service_credentials_request_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L861)
- [`get_service_credentials_confirm_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L868)
- [`get_admin_service_jobs_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L878)
- [`get_admin_service_job_details_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L901)
- [`get_shop_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L927)
- [`get_product_details_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L976)
- [`get_product_rules_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1027)
- [`get_product_pay_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1039)
- [`get_admin_inv_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1089)
- [`get_admin_inv_format_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1099)
- [`get_admin_users_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1110)
- [`get_admin_manage_usr_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1125)
- [`get_admin_user_activity_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1149)
- [`get_orders_menu_paginated`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1186)
- [`get_orders_rich_html`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1242)
- [`get_transactions_rich_html`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1255)
- [`get_admin_stats_rich_html`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1264)
- [`get_wallet_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1275)
- [`get_transaction_history_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1293)
- [`get_user_deposit_details_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1305)
- [`get_order_details_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1322)
- [`get_support_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/keyboards.py#L1409)

### customer_flows.py

- [`home_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py#L14)
- [`shop_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py#L23)
- [`orders_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py#L44)
- [`order_details`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_flows.py#L116)

### customer_purchase_ui.py

- [`summary`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_purchase_ui.py#L24)
- [`manifest`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_purchase_ui.py#L118)
- [`part_html`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_purchase_ui.py#L144)
- [`reopen`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/customer_purchase_ui.py#L190)

### deposit_ui.py

- [`deliver_receipt_media`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/deposit_ui.py#L41)
- [`deliver_receipt`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/deposit_ui.py#L87)
- [`format_deposit_rejection_reason_prompt`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/deposit_ui.py#L142)
- [`format_deposit_rejection_confirm`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/deposit_ui.py#L157)
- [`format_deposit_denied_success`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/deposit_ui.py#L174)
- [`format_deposit_detail_from_row`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/deposit_ui.py#L180)
- [`receipt_note`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/deposit_ui.py#L235)

### receipt_ui.py

- [`compact`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/receipt_ui.py#L8)
- [`price_details`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/receipt_ui.py#L37)
- [`status_text`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/receipt_ui.py#L47)

### admin_flows.py

- [`dashboard_summary`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py#L39)
- [`admin_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py#L64)
- [`products_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py#L205)
- [`product_detail_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py#L266)
- [`user_search_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py#L555)
- [`inventory_items_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py#L578)
- [`settings_display_menu`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_flows.py#L599)

### admin_navigation.py

- [`bind_markup`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_navigation.py#L126)
- [`paused_markup`](https://github.com/Farrokhzadeh/AccAbad-Shop/blob/66371a23a1c8149dc5bdf89a0355aa689d454dba/apps/bot/admin_navigation.py#L216)
