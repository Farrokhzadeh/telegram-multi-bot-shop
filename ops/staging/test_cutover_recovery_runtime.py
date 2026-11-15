import hashlib,hmac,json,os,re,shutil,signal,subprocess,sys,time
from pathlib import Path
from unittest.mock import patch
import cutover_inbox as inbox
from test_cutover_fence_runtime import wait_for,normalized
f=inbox.fence
p=f.pair
TABLES="brands brand_channels users admins admin_sessions products brand_products inventory_items wallets wallet_entries orders payments fulfillments supplier_requests jobs campaigns telegram_deliveries support_tickets support_messages legacy_id_map audit_events discounts discount_uses discount_revisions bot_activity_events supplier_request_attempts product_supplier_sources product_stock_subscriptions".split()
def snapshot(root,restore=None):
	m=f.model(root);observed={};original=subprocess.run
	def query(args,*a,**kw):
		if args[:2]!=["docker","run"] or "input" not in kw: return original(args,*a,**kw)
		side="legacy" if p.PG["legacy"] in args else "target"
		if side=="target":
			sql="SET LOCAL TIME ZONE 'UTC';\nSELECT 'T068_TABLES|'||string_agg(tablename,',' ORDER BY tablename) FROM pg_tables WHERE schemaname='shop';\n"
			for table in sorted(TABLES): sql+="SELECT 'T068_ROW|"+table+"|'||count(*)::text||':'||encode(sha256(convert_to(coalesce(string_agg(encode(sha256(convert_to(row_to_json(r)::text,'UTF8')),'hex'),'' ORDER BY encode(sha256(convert_to(row_to_json(r)::text,'UTF8')),'hex')),''),'UTF8')),'hex') FROM shop."+table+" r;\n"
			if restore is not None:
				mount="type=bind,source="+str(root/"target-db")+",target=/var/lib/postgresql";assert args.count(mount)==1;args=["type=bind,source="+str(restore)+",target=/var/lib/postgresql" if x==mount else x for x in args]
		else: sql="SELECT 'T068_SOURCE|'||(SELECT count(*) FROM orders)||'|'||(SELECT count(*) FROM inventory WHERE order_id IS NOT NULL)||'|'||(SELECT count(*) FROM transactions WHERE amount<0)||'|'||current_setting('transaction_read_only');\n"
		kw["input"]=kw["input"].replace(b"ROLLBACK;",sql.encode()+b"ROLLBACK;");result=original(args,*a,**kw);assert result.returncode==0
		log=root/("recovery-snapshot-"+side+"-"+str(time.time_ns())+".private.log");log.write_bytes(result.stdout+result.stderr)
		if side=="target":
			names=re.findall(rb'T068_TABLES\|([a-z_,]+)',result.stdout);assert names==[','.join(sorted(TABLES)).encode()]
			rows=re.findall(rb'T068_ROW\|([a-z_]+)\|(\d+:[a-f0-9]{64})',result.stdout+result.stderr);assert len(rows)==28 and len(dict(rows))==28;observed[side]={k.decode():v.decode() for k,v in rows}
		else:
			rows=re.findall(rb'T068_SOURCE\|(\d+)\|(\d+)\|(\d+)\|on',result.stdout);assert len(rows)==1;observed[side]=list(map(int,rows[0]))
		return result
	with patch.object(f.subprocess,"run",side_effect=query): stock=f.audit(root,m,f.header(root,m))
	return {"stock":{s:v["used"] for s,v in stock.items()},"rows":observed}
def run(root):
	m=f.model(root);p.validate(root,m);s=f.chain(root,m)[-1];assert s["phase"]=="frozen" and not s["target_seen"] and f.stopped(m);c=inbox.config(root);phases=[]
	stream=(root/"recovery-docker-events.private.jsonl").open("wb");errors=(root/"recovery-docker-events-errors.private.log").open("wb");capture=subprocess.Popen(["docker","events","--since",str(int(time.time())),"--filter","container="+m["containers"]["legacy-db"]["id"],"--filter","container="+m["containers"]["target-db"]["id"],"--filter","event=start","--filter","event=die","--format","{{json .}}"],stdout=stream,stderr=errors);time.sleep(.2);assert capture.poll() is None
	def record(name):
		phases.append(name);p.save(root/"recovery-runtime-progress.private.json",{"run":m["run"],"phases":phases})
	def event(n,action):
		v={"run":c["run"],"brand":c["brand"],"channel":"telegram","origin":c["origins"]["telegram"],"actor":c["actor"],"event":str(n),"action":action};body=f.canonical(v);signature=hmac.new(bytes.fromhex(p.private(root/"inbox-ingress.private").strip()),body,hashlib.sha256).hexdigest();return inbox.capture(root,body,signature)["event"]
	def denied(fn):
		try: fn()
		except AssertionError: return
		raise AssertionError("Unsafe reverse/replay was accepted")
	def status(side,epoch):
		request=f.admit(root,side,epoch,"status")["request"];path=root/side/"results"/(request+".json");wait_for(path.is_file);return normalized(json.loads(path.read_text()))
	try:
		before=snapshot(root);assert before["stock"]=={"legacy":0,"target":0} and before["rows"]["legacy"]==[1,1,1]
		p.save(root/"recovery-before.private.json",f.sign(root,before));s=f.grant(root,"legacy",s["epoch"]);assert status("legacy",s["epoch"])["orders"]==1;s=f.freeze(root,s["epoch"])
		held=event(201,"wallet-paused");assert f.stopped(m);s=f.grant(root,"legacy",s["epoch"]);assert status("legacy",s["epoch"])["orders"]==1;record("source-owner-resumed-before-target-acceptance")
		requests_before=len(list((root/"fence-requests").iterdir()))
		for command in f.TARGET_COMMANDS: denied(lambda command=command:f.admit(root,"legacy",s["epoch"],command))
		assert len(list((root/"fence-requests").iterdir()))==requests_before
		inbox.dispatch(root,held,"legacy",s["epoch"]);wait_for(lambda:(root/"legacy"/("held-"+held+".json")).is_file());s=f.freeze(root,s["epoch"]);after=snapshot(root);assert after==before and inbox.reconcile(root,held)["phase"]=="uncertain_do_not_retry";record("source-interruption-left-all-target-tables-unchanged")
		accepted=event(202,"wallet");s=f.grant(root,"target",s["epoch"]);inbox.dispatch(root,accepted,"target",s["epoch"]);path=root/"target/results"/(accepted+".json");wait_for(path.is_file);assert normalized(json.loads(path.read_text()))["outcome"]=="Purchased";assert inbox.reconcile(root,accepted)["phase"]=="result_observed"
		denied(lambda:f.admit(root,"target",s["epoch"]-1,"manifest-worker"))
		for _ in range(2):
			request=f.admit(root,"target",s["epoch"],"manifest-worker")["request"];result=root/"target/results"/(request+".json");wait_for(result.is_file);assert normalized(json.loads(result.read_text()))["outcome"]=="PreparedWithoutSend"
		record("current-target-manifest-claims-fenced-and-preserved")
		code="import os,signal,sys;from pathlib import Path;from unittest.mock import patch;import cutover_fence as f\nwith patch.object(f,'append',side_effect=lambda *a,**k:os.kill(os.getpid(),signal.SIGKILL)): f.freeze(Path(sys.argv[1]),int(sys.argv[2]))\n"
		with (root/"recovery-freeze-crash.private.log").open("wb") as log: crash=subprocess.run([sys.executable,"-c",code,str(root),str(s["epoch"])],env={**os.environ,"PYTHONPATH":str(root/"tools")},stdout=log,stderr=subprocess.STDOUT,timeout=180)
		(root/"recovery-freeze-crash.exit").write_text(str(crash.returncode)+"\n");assert crash.returncode==-signal.SIGKILL and f.stopped(m);denied(lambda:f.admit(root,"target",s["epoch"],"wallet"));s=f.recover(root);assert s["phase"]=="frozen" and s["owner"] is None;denied(lambda:f.grant(root,"legacy",s["epoch"]));record("post-acceptance-freeze-sigkill-recovered-without-old-writer")
		accepted_state=snapshot(root);assert accepted_state["stock"]=={"legacy":0,"target":1} and accepted_state["rows"]["legacy"]==[1,1,1] and accepted_state["rows"]["target"]!=before["rows"]["target"];p.save(root/"recovery-accepted.private.json",f.sign(root,accepted_state))
		backup=root/"rollback-backup";backup.mkdir(mode=0o700)
		for name in ["target-db","target","inbox-events","fence-ledger","fence-requests"]: p.copy_owned(root/name,backup/name)
		for name in ["pair.private.json","authority.private.json","fence-header.private.json","inbox.private.json","fence-key.private","inbox-ingress.private"]:
			original=p.private(root/name);shutil.copy2(root/name,backup/name);assert p.private(backup/name)==original
		fingerprints={name:p.tree_fingerprint(root/name) for name in ["target-db","target","inbox-events","fence-ledger","fence-requests"]}
		for name,v in fingerprints.items(): assert p.tree_fingerprint(backup/name)==v
		p.save(root/"rollback-backup-manifest.private.json",f.sign(root,{"run":m["run"],"directories":fingerprints,"files":{name:p.digest(path) for path in backup.iterdir() if path.is_file() for name in [path.name]}}))
		restored=root/"rollback-restore";restored.mkdir(mode=0o700);p.copy_owned(backup/"target-db",restored/"target-db");assert p.tree_fingerprint(restored/"target-db")==fingerprints["target-db"]
		assert snapshot(root,restored/"target-db")==accepted_state;record("independent-restored-database-preserved-all-28-tables")
		s=f.grant(root,"target",s["epoch"]);assert status("target",s["epoch"])["orders"]==2;denied(lambda:inbox.dispatch(root,accepted,"target",s["epoch"]));denied(lambda:inbox.dispatch(root,held,"target",s["epoch"]));s=f.freeze(root,s["epoch"]);assert snapshot(root)==accepted_state;record("target-restart-preserved-accepted-state-and-replay-fences")
		time.sleep(.5);capture.terminate();capture.wait(timeout=10);stream.close();errors.close();running=set();starts=0;ids={m["containers"][side+"-db"]["id"] for side in f.SIDES}
		for line in (root/"recovery-docker-events.private.jsonl").read_text().splitlines():
			e=json.loads(line);id=e["Actor"]["ID"];assert id in ids
			if e["Action"]=="start": running.add(id);starts+=1;assert len(running)==1
			elif e["Action"]=="die": running.discard(id)
		assert starts==4 and not running and f.stopped(m)
		p.save(root/"recovery-runtime-result.private.json",{"run":m["run"],"phases":phases,"database_starts_observed":starts,"overlap":False,"freeze_coordinator_sigkill_exit":crash.returncode,"all_28_target_tables_restored":True,"accepted_effects_preserved":True,"source_reverse_after_acceptance_rejected":True,"preacceptance_source_resume_verified":True,"all_four_stopped":True,"backup_manifest_sha256":f.digest(fingerprints),"protected_copy_rollback_verified":False,"postacceptance_reverse_replay_to_legacy_verified":False,"task_complete":False,"release_ready":False})
	finally:
		p.stop(root,m)
		if capture.poll() is None: capture.terminate();capture.wait(timeout=10)
		stream.close();errors.close()
if __name__=="__main__":
	import argparse
	a=argparse.ArgumentParser();a.add_argument("--root",required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
