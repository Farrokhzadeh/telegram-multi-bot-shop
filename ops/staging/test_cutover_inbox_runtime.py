import hashlib,hmac,json,os,signal,subprocess,sys,time
from pathlib import Path
import cutover_inbox as inbox
from test_cutover_fence_runtime import wait_for,normalized
f=inbox.fence
p=f.pair
def run(root):
	m=f.model(root);p.validate(root,m);s=f.chain(root,m)[-1];assert s["phase"]=="frozen" and f.stopped(m);c=inbox.config(root);phases=[]
	stream=(root/"inbox-docker-events.private.jsonl").open("wb");errors=(root/"inbox-docker-events-errors.private.log").open("wb")
	capture=subprocess.Popen(["docker","events","--since",str(int(time.time())),"--filter","container="+m["containers"]["legacy-db"]["id"],"--filter","container="+m["containers"]["target-db"]["id"],"--filter","event=start","--filter","event=die","--format","{{json .}}"],stdout=stream,stderr=errors)
	time.sleep(.2);assert capture.poll() is None
	def record(name):
		phases.append(name);p.save(root/"inbox-runtime-progress.private.json",{"run":m["run"],"phases":phases})
	def event(n,channel="telegram",action=None):
		v={"run":c["run"],"brand":c["brand"],"channel":channel,"origin":c["origins"][channel],"actor":c["actor"],"event":str(n),"action":action or ("wallet" if channel=="telegram" else "review")};body=f.canonical(v);signature=hmac.new(bytes.fromhex(p.private(root/"inbox-ingress.private").strip()),body,hashlib.sha256).hexdigest();return inbox.capture(root,body,signature)
	def denied(fn):
		try: fn()
		except AssertionError: return
		raise AssertionError("Unsafe replay was accepted")
	try:
		s=f.grant(root,"legacy",s["epoch"]);held=event(101,action="wallet-paused")["event"];inbox.dispatch(root,held,"legacy",s["epoch"])
		wait_for(lambda:(root/"legacy"/("held-"+held+".json")).is_file())
		command='export PGPASSWORD="$POSTGRES_PASSWORD"; exec psql -X -w -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -U "$POSTGRES_USER" -d t068_legacy_stage -Atc "$1"'
		r=subprocess.run(["docker","exec",m["containers"]["legacy-db"]["id"],"sh","-c",command,"probe","SELECT count(*) FROM pg_stat_activity WHERE application_name='t068-cutover-stage' AND state='idle in transaction' AND xact_start IS NOT NULL;"],capture_output=True,timeout=15);assert r.returncode==0 and int(r.stdout.strip())==1
		s=f.freeze(root,s["epoch"]);assert s["stock"]["legacy"]["used"]==0 and f.stopped(m)
		assert inbox.reconcile(root,held)["phase"]=="uncertain_do_not_retry";denied(lambda:inbox.dispatch(root,held,"target",s["epoch"]))
		record("interrupted-source-input-retained-without-retry")
		pending=event(102)["event"];payment=event(103,"payment")["event"];assert f.stopped(m) and event(102)["duplicate"]
		assert inbox.dispatch(root,payment,None,None)["phase"]=="held";denied(lambda:inbox.dispatch(root,pending,"target",s["epoch"]))
		record("freeze-window-inputs-durable-payment-held")
		s=f.grant(root,"target",s["epoch"]);denied(lambda:inbox.dispatch(root,pending,"legacy",s["epoch"]));denied(lambda:inbox.dispatch(root,pending,"target",s["epoch"]-1))
		code="import os,signal,sys;from pathlib import Path;from unittest.mock import patch;import cutover_inbox as i\nroot=Path(sys.argv[1]);admit=i.fence.admit\ndef crash(*a):\n admit(*a);os.kill(os.getpid(),signal.SIGKILL)\nwith patch.object(i.fence,'admit',side_effect=crash): i.dispatch(root,sys.argv[2],'target',int(sys.argv[3]))\n"
		with (root/"inbox-coordinator-crash.private.log").open("wb") as log: r=subprocess.run([sys.executable,"-c",code,str(root),pending,str(s["epoch"])],env={**os.environ,"PYTHONPATH":str(root/"tools")},stdout=log,stderr=subprocess.STDOUT,timeout=180)
		(root/"inbox-coordinator-crash.exit").write_text(str(r.returncode)+"\n");assert r.returncode==-signal.SIGKILL
		path=root/"target/results"/(pending+".json");wait_for(path.is_file);assert normalized(json.loads(path.read_text()))["outcome"]=="Purchased"
		assert inbox.reconcile(root,pending)["phase"]=="result_observed";assert event(102)["duplicate"];denied(lambda:inbox.dispatch(root,pending,"target",s["epoch"]))
		record("target-accepted-after-coordinator-sigkill-reconciled")
		s=f.freeze(root,s["epoch"]);assert s["stock"]["legacy"]["used"]==0 and s["stock"]["target"]["used"]==1
		s=f.grant(root,"target",s["epoch"]);assert inbox.reconcile(root,pending)["phase"]=="result_observed";denied(lambda:inbox.dispatch(root,held,"target",s["epoch"]))
		request=f.admit(root,"target",s["epoch"],"status")["request"];status=root/"target/results"/(request+".json");wait_for(status.is_file);assert normalized(json.loads(status.read_text()))["orders"]==2
		s=f.freeze(root,s["epoch"]);assert s["stock"]["legacy"]["used"]==0 and s["stock"]["target"]["used"]==1
		assert inbox.inventory(root)=={"dispatching":1,"result_observed":1,"held":1};record("restart-preserved-effects-and-uncertainty")
		time.sleep(.5);capture.terminate();capture.wait(timeout=10);stream.close();errors.close();running=set();starts=0;ids={m["containers"][side+"-db"]["id"] for side in f.SIDES}
		for line in (root/"inbox-docker-events.private.jsonl").read_text().splitlines():
			e=json.loads(line);id=e.get("Actor",{}).get("ID");action=e.get("Action")
			if id not in ids: continue
			if action=="start": running.add(id);starts+=1;assert len(running)==1
			elif action=="die": running.discard(id)
		assert starts==3 and not running and f.stopped(m)
		p.save(root/"inbox-runtime-result.private.json",{"run":m["run"],"phases":phases,"database_starts_observed":starts,"overlap":False,"common_unit_legacy_used":0,"common_unit_target_used":1,"captured_events":3,"uncertain_inputs":1,"held_payment_notices":1,"observed_checkout_results":1,"coordinator_sigkill_exit":r.returncode,"all_four_stopped":True,"real_transport_verified":False,"protected_copy_rehearsal_verified":False,"rollback_verified":False,"task_complete":False,"release_ready":False})
	finally:
		p.stop(root,m)
		if capture.poll() is None: capture.terminate();capture.wait(timeout=10)
		stream.close();errors.close()
if __name__=="__main__":
	import argparse
	a=argparse.ArgumentParser();a.add_argument("--root",required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
