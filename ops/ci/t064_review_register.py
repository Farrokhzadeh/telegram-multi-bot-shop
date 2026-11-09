import argparse
import collections
import hashlib
import json
import os
import pathlib
import re
import stat
import uuid

SOURCES={"prod1":"acc-abad","prod2":"kuala","prod3":"zikotech"}
REVIEW="wallet-shared-direct-catalog-review.json"

def private_file(path):
	if path.is_symlink() or not path.is_file() or path.stat().st_mode & 0o077:
		raise ValueError("Private evidence path rejected")
	return path.read_bytes()

def verify(root,evidence):
	if root.is_symlink() or not root.is_dir() or root.stat().st_mode & 0o077:
		raise ValueError("Private archive directory rejected")
	public=json.loads(evidence)
	brands={row["source"]:row for row in public["brands"]}
	if set(brands)!=set(SOURCES):
		raise ValueError("Unexpected source set")
	all_counts=collections.Counter()
	results={}
	for source,brand in SOURCES.items():
		path=root/("teleg-history-archive-"+source)
		if path.is_symlink() or not path.is_dir() or path.stat().st_mode & 0o077:
			raise ValueError("Private source directory rejected")
		private=json.loads(private_file(path/REVIEW))
		source_plan=hashlib.sha256(private_file(path/"archive.plan")).hexdigest()
		if private["archive_manifest_sha256"]!=source_plan or not re.fullmatch("[0-9a-f]{64}",private["typed_manifest_sha256"]):
			raise ValueError("Protected plan commitment mismatch")
		review=private["review"]
		if len(review)!=private["reviewed_records"] or private["reviewed_records"]<0 or private["typed_orders"]!=brands[source]["selected_typed_orders"]:
			raise ValueError("Review coverage mismatch")
		keys=set()
		reasons=collections.Counter()
		orders=collections.Counter()
		for row in review:
			if set(row)!={"projection","legacy_id","target","reason"} or not isinstance(row["projection"],str) or not isinstance(row["legacy_id"],str) or not isinstance(row["reason"],str) or not re.fullmatch("[a-z_]{1,80}",row["reason"]):
				raise ValueError("Malformed review entry")
			if not row["projection"] or not row["legacy_id"] or uuid.UUID(row["target"]).int==0:
				raise ValueError("Invalid review identity")
			if (row["projection"],row["legacy_id"]) in keys:
				raise ValueError("Duplicate review identity")
			keys.add((row["projection"],row["legacy_id"]))
			reasons[row["reason"]]+=1
			if row["projection"]=="OrderRecord":
				orders[row["reason"]]+=1
		if dict(reasons)!=private["review_reason_counts"]:
			raise ValueError("Review reason mismatch")
		if sum(orders.values())!=brands[source]["held_for_review"] or private["typed_orders"]+sum(orders.values())!=brands[source]["original_orders"]:
			raise ValueError("Source order coverage mismatch")
		if orders.get("incomplete_order_terms",0)!=sum(brands[source][key] for key in ("missing_checkout_snapshot","original_wallet_irr_terms","nonterminating_original_bank_unit")) or orders.get("missing_exact_settled_source_payment",0)!=brands[source]["settled_payment_predates_order"]:
			raise ValueError("Protected exception reason mismatch")
		if any(key not in ("incomplete_order_terms","missing_exact_settled_source_payment") for key in orders):
			raise ValueError("Unreviewed order reason")
		all_counts.update({"original_orders":brands[source]["original_orders"],"typed_orders":private["typed_orders"],"review_orders":sum(orders.values()),"review_records":len(review)})
		results[source]={"original_orders":brands[source]["original_orders"],"typed_orders":private["typed_orders"],"review_orders":sum(orders.values()),"private_review_rows":len(review),"source_plan_match":True,"unique_review_keys":True}
	totals=public["totals"]
	if all_counts["original_orders"]!=totals["source_original_orders"] or all_counts["typed_orders"]!=totals["canonical_qualified_selected_orders"] or all_counts["review_orders"]!=totals["protected_exception_orders"]:
		raise ValueError("Cross-brand order coverage mismatch")
	return {"scope":"owner_private_t064_review_register_integrity_only","sources":results,"totals":dict(all_counts),"private_case_ids_exposed":False,"money_or_delivery_adjudicated":False,"release_ready":False,"matched":True}

def main():
	parser=argparse.ArgumentParser()
	parser.add_argument("--archive-root",type=pathlib.Path,required=True)
	parser.add_argument("--evidence-file",type=pathlib.Path,required=True)
	parser.add_argument("--receipt-file",type=pathlib.Path,required=True)
	args=parser.parse_args()
	out=args.receipt_file
	if not out.is_absolute() or out.exists() or out.parent.is_symlink() or not out.parent.is_dir() or out.parent.stat().st_mode & 0o077:
		raise ValueError("Private receipt path rejected")
	result=verify(args.archive_root,private_file(args.evidence_file))
	fd=os.open(str(out),os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
	with os.fdopen(fd,"w") as file:
		json.dump(result,file,sort_keys=True,indent=2)
		file.write("\n")
		file.flush()
		os.fsync(file.fileno())
	print("PASS: protected T064 review register coverage; no liability disposition or release")
if __name__=="__main__":
	main()
