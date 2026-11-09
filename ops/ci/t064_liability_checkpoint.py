import argparse
import datetime
import json
import os
import pathlib
import stat
import sys

BRANDS=("prod1","prod2","prod3")
FILES={
	"orders":"legacy-order-payment-exception-disposition-2026-11-09.json",
	"coupons":"legacy-coupon-obligation-preflight-2026-11-08.json",
	"delivery":"legacy-delivery-liability-preflight-2026-11-08.json",
	"wallet":"legacy-wallet-retention-2026-11-06.json"
}

def require(condition):
	if not condition:
		raise ValueError("T064 historical liability evidence rejected")

def by_source(rows):
	out={row["source"]:row for row in rows}
	require(len(rows)==len(BRANDS) and set(out)==set(BRANDS))
	return out

def verify(orders,coupons,delivery,wallet,review,history,assets,asset_date):
	o=by_source(orders["brands"])
	c=by_source(coupons["sources"])
	d=by_source(delivery["sources"])
	w=by_source(wallet["sources"])
	r=review["sources"]
	require(set(r)==set(BRANDS) and set(assets["instances"])==set(BRANDS))
	require(orders["qualification"]["release_ready"] is False and orders["qualification"]["original_paid_obligations_or_owner_decisions_resolved"] is False)
	require(orders["preservation"]["unknown_fulfillment_or_money_liabilities_waived"] is False and orders["preservation"]["original_irr_converted_to_usd"] is False)
	require(coupons["guards"]["discounts_activated"] is False and coupons["guards"]["unowned_prepaid_ownership_inferred"] is False and coupons["guards"]["release_ready"] is False)
	require(delivery["limits"]["source_sent_marker_is_delivery_acknowledgement"] is False and delivery["limits"]["source_sent_marker_authorizes_target_delivery"] is False and delivery["limits"]["original_inventory_units_are_saleable_target_stock"] is False and delivery["limits"]["release_ready"] is False)
	require(wallet["register_replay_identical"] is True and wallet["release_ready"] is False and wallet["operational_postings"]==0)
	require(review["matched"] is True and review["release_ready"] is False and history["matched"] is True and history["release_ready"] is False)
	require(history["total_members"]==sum(v["source_users"] for v in w.values())==12282)
	require(assets["release_ready"] is False)
	original=typed=held=unclear=irr_orders=precision=chronology=sent=discounts=unowned=balances=history_rows=0
	media={"verified_receipts":0,"missing_receipts":0,"no_local_reference":0,"unverified_product_media":0}
	sources={}
	for s in BRANDS:
		a,b,e,f,g=o[s],c[s],d[s],w[s],r[s]
		require(a["original_orders"]==g["original_orders"]==e["original_orders"]==a["selected_typed_orders"]+a["held_for_review"])
		require(a["selected_typed_orders"]==g["typed_orders"] and a["held_for_review"]==g["review_orders"])
		require(a["held_for_review"]==sum(a[k] for k in ("missing_checkout_snapshot","original_wallet_irr_terms","nonterminating_original_bank_unit","settled_payment_predates_order")))
		require(e["delivery_headers"]==e.get("source_sent_markers",0)==e.get("delivery_headers_with_original_order",0))
		require(b["original_discounts"]==b["assigned_owner"]+b["unassigned_owner"])
		require(f["source_users"]=={"prod1":9277,"prod2":12,"prod3":2993}[s])
		v=assets["instances"][s]
		require(v["inspection_complete"] is True and v["release_ready"] is False)
		require(v["source_authentication"]==("not_exercised" if s=="prod2" else "passed"))
		m=v.get("receipt_files",{})
		require(isinstance(m,dict))
		original+=a["original_orders"]
		typed+=a["selected_typed_orders"]
		held+=a["held_for_review"]
		unclear+=a["missing_checkout_snapshot"]
		irr_orders+=a["original_wallet_irr_terms"]
		precision+=a["nonterminating_original_bank_unit"]
		chronology+=a["settled_payment_predates_order"]
		sent+=e.get("source_sent_markers",0)
		discounts+=b["original_discounts"]
		unowned+=b["unassigned_owner"]
		balances+=f["preserved_nonzero_irr_balances"]
		history_rows+=f["preserved_history"]
		media["verified_receipts"]+=m.get("verified_hash",0)
		media["missing_receipts"]+=m.get("missing",0)
		media["no_local_reference"]+=m.get("no_local_reference",0)
		media["unverified_product_media"]+=v.get("product_media_references_unverified",0)
		sources[s]={"original_orders":a["original_orders"],"typed_orders":a["selected_typed_orders"],"retained_order_exceptions":a["held_for_review"],"irr_liability_accounts":f["preserved_nonzero_irr_balances"],"unassigned_coupon_records":b["unassigned_owner"],"historical_sent_markers":e.get("source_sent_markers",0)}
	totals=orders["totals"]
	require((original,typed,held,unclear,irr_orders,precision,chronology,sent)==(4916,919,3997,3819,174,2,2,899))
	require((totals["source_original_orders"],totals["canonical_qualified_selected_orders"],totals["protected_exception_orders"],totals["missing_checkout_snapshot"],totals["original_irr_wallet_terms"],totals["nonterminating_original_bank_unit"],totals["confirmed_external_payment_before_original_order_creation"],totals["original_delivery_sent_markers_total"])==(original,typed,held,unclear,irr_orders,precision,chronology,sent))
	require((review["totals"]["original_orders"],review["totals"]["typed_orders"],review["totals"]["review_orders"])==(original,typed,held))
	require(discounts==coupons["totals"]["original_discounts"]==123 and unowned==coupons["totals"]["unassigned_owner"]==21)
	require(sent==delivery["totals"]["original_sent_markers"]==delivery["totals"]["delivery_headers"])
	require((balances,history_rows)==(40,7502))
	require(media=={"verified_receipts":3901,"missing_receipts":288,"no_local_reference":536,"unverified_product_media":3})
	require(asset_date=="2026-11-05")
	return {"scope":"t064_unresolved_historical_liability_and_media_checkpoint","source_media_report_date_utc":asset_date,"sources":sources,
		"totals":{"original_orders":original,"qualified_typed_orders":typed,"protected_order_exceptions":held,"missing_checkout_snapshots":unclear,"original_irr_order_terms":irr_orders,"precision_exceptions":precision,"chronology_exceptions":chronology,"irr_liability_accounts":balances,"historical_ledger_records":history_rows,"historical_coupons":discounts,"unassigned_coupon_records":unowned,"historical_sent_markers":sent,**media},
		"decisions_required_now":False,"future_owner_review_required":True,"missing_media_is_not_forgiven":True,"historical_sent_is_not_receipt_proof":True,"no_currency_conversion_or_payment_reconstruction":True,"financial_liabilities_resolved":False,"live_customer_history_serving_qualified":False,"release_ready":False,"matched":True}

def private_json(path):
	info=path.lstat()
	require(stat.S_ISREG(info.st_mode) and not path.is_symlink() and info.st_mode & 0o077==0 and info.st_size<8388608)
	return json.loads(path.read_bytes())

def main():
	parser=argparse.ArgumentParser()
	parser.add_argument("--evidence-root",type=pathlib.Path,required=True)
	parser.add_argument("--private-root",type=pathlib.Path,required=True)
	parser.add_argument("--asset-summary",type=pathlib.Path,required=True)
	parser.add_argument("--receipt",type=pathlib.Path,required=True)
	args=parser.parse_args()
	try:
		require(args.private_root.is_dir() and not args.private_root.is_symlink() and args.private_root.stat().st_mode & 0o077==0)
		require(args.receipt.is_absolute() and not args.receipt.exists() and args.receipt.parent.is_dir() and args.receipt.parent.stat().st_mode & 0o077==0)
		data={k:private_json(args.evidence_root/name) for k,name in FILES.items()}
		review=private_json(args.private_root/"t064-review-register-4965bb4/result.json")
		history=private_json(args.private_root/"t064-history-files-f0af725/result.json")
		assets=private_json(args.asset_summary)
		date=datetime.datetime.fromtimestamp(args.asset_summary.stat().st_mtime,datetime.timezone.utc).date().isoformat()
		result=verify(**data,review=review,history=history,assets=assets,asset_date=date)
		fd=os.open(args.receipt,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
		with os.fdopen(fd,"w") as file:
			json.dump(result,file,sort_keys=True,indent=2)
			file.write("\n")
			file.flush()
			os.fsync(file.fileno())
		print("PASS: T064 protected liability and historical media cross-evidence matched; all release gates held")
	except (OSError,KeyError,TypeError,ValueError,json.JSONDecodeError):
		print("FAIL: T064 protected evidence mismatch; details retained privately",file=sys.stderr)
		return 1
	return 0

if __name__=="__main__":
	sys.exit(main())
