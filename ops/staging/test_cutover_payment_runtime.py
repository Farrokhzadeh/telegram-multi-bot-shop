import hashlib,hmac,json,os,signal,subprocess,sys,time,uuid
from pathlib import Path
import cutover_inbox as inbox
import cutover_payment as payment
from test_cutover_fence_runtime import wait_for,normalized
from test_cutover_recovery_runtime import snapshot
f=inbox.fence
p=f.pair
def run(root):
	m=f.model(root);p.validate(root,m);s=f.chain(root,m)[-1];assert s["phase"]=="frozen" and f.stopped(m);key=payment.key(root);config=inbox.config(root);phases=[]
	stream=(root/"payment-docker-events.private.jsonl").open("wb");errors=(root/"payment-docker-events-errors.private.log").open("wb");events=subprocess.Popen(["docker","events","--since",str(int(time.time())),"--filter","container="+m["containers"]["legacy-db"]["id"],"--filter","container="+m["containers"]["target-db"]["id"],"--filter","event=start","--filter","event=die","--format","{{json .}}"],stdout=stream,stderr=errors);time.sleep(.2);assert events.poll() is None
	def record(name):
		phases.append(name);p.save(root/"payment-runtime-progress.private.json",{"run":m["run"],"phases":phases})
	def notice(status="finished",quote=10.25,origin=None,formatted=False,invalid=False):
		v={"actually_paid":10.4,"order_id":origin or key["origin"],"pay_amount":10.4,"pay_currency":"usdtbsc","payment_id":int(key["provider"]),"payment_status":status,"price_amount":quote,"price_currency":"usd"};canonical=f.canonical(v);signature="0"*128 if invalid else hmac.new(key["secret"].encode(),canonical,hashlib.sha512).hexdigest();return {"run":m["run"],"brand":config["brand"],"channel":"payment","origin":config["origins"]["payment"],"actor":config["actor"],"event":hashlib.sha256(bytes.fromhex(signature)).hexdigest(),"action":"payment","body":("\n"+canonical.decode()+"\n") if formatted else canonical.decode(),"signature":signature}
	def capture(v):
		raw=f.canonical(v);signature=hmac.new(bytes.fromhex(p.private(root/"inbox-ingress.private").strip()),raw,hashlib.sha256).hexdigest();return inbox.capture(root,raw,signature)
	def denied(fn):
		try: fn()
		except AssertionError: return
		raise AssertionError("Unexpected payment admission")
	def outcome(request):
		path=root/"target/results"/(request+".json");wait_for(path.is_file);return normalized(json.loads(path.read_text()))["outcome"]
	def routed(v,expected):
		e=capture(v)["event"];inbox.dispatch(root,e,"target",s["epoch"]);assert outcome(e)==expected;observed=inbox.reconcile(root,e);assert observed["outcome"]=="payment:"+expected;return e
	def direct(v,expected):
		request=uuid.uuid4().hex;data=payment.payload(v,uuid.uuid4().hex);path=root/"target/payment"/(request+".json");p.save(path,data);os.chown(path,10001,10001);f.sync(path.parent);f.admit(root,"target",s["epoch"],"payment",request);assert outcome(request)==expected;p.save(root/("payment-probe-"+request+".private.json"),{"request":request,"event":v["event"],"expected":expected,"payload_sha256":p.digest(path),"result_sha256":p.digest(root/"target/results"/(request+".json"))})
	try:
		before=snapshot(root);p.save(root/"payment-before.private.json",f.sign(root,before));s=f.grant(root,"legacy",s["epoch"]);ready=notice();first=capture(ready)["event"];denied(lambda:inbox.dispatch(root,first,"legacy",s["epoch"]));s=f.freeze(root,s["epoch"]);assert f.stopped(m);assert capture(ready)["duplicate"] and capture(notice(formatted=True))["duplicate"];denied(lambda:capture(notice(invalid=True)));unbound=notice(origin="unbound-stage-"+m["run"]);held=capture(unbound)["event"];record("real-signature-verification-and-durable-capture-with-both-writers-frozen")
		s=f.grant(root,"target",s["epoch"]);denied(lambda:inbox.dispatch(root,first,"target",s["epoch"]-1))
		code="import os,signal,sys,time;from pathlib import Path;import cutover_inbox as i\nroot=Path(sys.argv[1]);i.dispatch(root,sys.argv[2],'target',int(sys.argv[3]));end=time.monotonic()+90\nwhile time.monotonic()<end:\n if (root/'target/results'/(sys.argv[2]+'.json')).is_file(): os.kill(os.getpid(),signal.SIGKILL)\n time.sleep(.1)\nraise SystemExit(124)\n"
		with (root/"payment-coordinator-crash.private.log").open("wb") as log: crash=subprocess.run([sys.executable,"-c",code,str(root),first,str(s["epoch"])],env={**os.environ,"PYTHONPATH":str(root/"tools")},stdout=log,stderr=subprocess.STDOUT,timeout=120)
		(root/"payment-coordinator-crash.exit").write_text(str(crash.returncode)+"\n");assert crash.returncode==-signal.SIGKILL and outcome(first)=="Ready";assert inbox.reconcile(root,first)["outcome"]=="payment:Ready";direct(ready,"Replayed");direct(notice(formatted=True),"Replayed");record("actual-evidence-store-commit-and-encrypted-archive-survived-coordinator-sigkill")
		inbox.dispatch(root,held,"target",s["epoch"]);assert outcome(held)=="Held" and inbox.reconcile(root,held)["phase"]=="held";routed(notice(status="confirming"),"Replayed");anomaly=notice(quote=99);routed(anomaly,"Review");record("authentic-unbound-held-late-pending-preserved-and-anomaly-reviewed-without-posting")
		s=f.freeze(root,s["epoch"]);after=snapshot(root);changed={name for name,value in before["rows"]["target"].items() if after["rows"]["target"][name]!=value};assert changed=={"payments","jobs","bot_activity_events"} and before["stock"]==after["stock"]=={"legacy":0,"target":0} and before["rows"]["legacy"]==after["rows"]["legacy"]==[1,1,1];p.save(root/"payment-after.private.json",f.sign(root,after));s=f.grant(root,"target",s["epoch"]);direct(anomaly,"Replayed");s=f.freeze(root,s["epoch"]);assert snapshot(root)==after;assert inbox.inventory(root)=={"result_observed":3,"held":1};assert len(list((root/"target/payment-archives").glob("*.json")))==4;record("restart-replay-preserved-all25-nonpayment-tables-and-verification-obligation")
		time.sleep(.5);events.terminate();events.wait(timeout=10);stream.close();errors.close();running=set();starts=0;ids={m["containers"][side+"-db"]["id"] for side in f.SIDES}
		for line in (root/"payment-docker-events.private.jsonl").read_text().splitlines():
			e=json.loads(line);id=e["Actor"]["ID"];assert id in ids
			if e["Action"]=="start": running.add(id);starts+=1;assert len(running)==1
			elif e["Action"]=="die": running.discard(id)
		assert starts==3 and not running and f.stopped(m);p.save(root/"payment-runtime-result.private.json",{"run":m["run"],"phases":phases,"database_starts_observed":starts,"overlap":False,"coordinator_sigkill_exit":crash.returncode,"captured_payment_events":4,"held_authentic_unbound_events":1,"encrypted_raw_archives":4,"direct_replay_probes":3,"unchanged_nonpayment_tables":25,"all_four_stopped":True,"actual_ipn_verifier_and_evidence_store_used":True,"real_provider_used":False,"settlement_worker_used":False,"legacy_callback_dispatch_verified":False,"actual_shared_core_lifecycle_verified":False,"protected_rehearsal_verified":False,"step3_complete":False,"task_complete":False,"release_ready":False})
	finally:
		p.stop(root,m)
		if events.poll() is None: events.terminate();events.wait(timeout=10)
		stream.close();errors.close()
if __name__=="__main__":
	import argparse
	a=argparse.ArgumentParser();a.add_argument("--root",required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
