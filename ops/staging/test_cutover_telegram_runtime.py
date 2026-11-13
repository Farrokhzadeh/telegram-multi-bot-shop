import hashlib,hmac,json,os,signal,subprocess,sys,time,uuid
from pathlib import Path
import cutover_inbox as inbox
from test_cutover_fence_runtime import wait_for,normalized
from test_cutover_recovery_runtime import snapshot
f=inbox.fence
p=f.pair
def run(root):
	m=f.model(root);p.validate(root,m);s=f.chain(root,m)[-1];assert s["phase"]=="frozen" and f.stopped(m);c=inbox.config(root);key=inbox.telegram_binding(root);header=__import__('base64').urlsafe_b64encode(bytes.fromhex(key["secret"])).decode().rstrip("=");product=f.owned(root,"target","state.json")["product"];phases=[]
	stream=(root/"telegram-docker-events.private.jsonl").open("wb");errors=(root/"telegram-docker-events-errors.private.log").open("wb");capture=subprocess.Popen(["docker","events","--since",str(int(time.time())),"--filter","container="+m["containers"]["legacy-db"]["id"],"--filter","container="+m["containers"]["target-db"]["id"],"--filter","event=start","--filter","event=die","--format","{{json .}}"],stdout=stream,stderr=errors);time.sleep(.2);assert capture.poll() is None
	def record(name):
		phases.append(name);p.save(root/"telegram-runtime-progress.private.json",{"run":m["run"],"phases":phases})
	def body(n,revision=2,actor=7001,bot=1001,target=None,menu=False):
		message={"message_id":77,"chat":{"id":actor,"type":"private"}}
		if menu: return {"update_id":n,"message":{**message,"from":{"id":actor,"is_bot":False},"text":"/menu"}}
		return {"update_id":n,"callback_query":{"from":{"id":actor,"is_bot":False},"data":"v1|"+str(revision)+"|p|"+uuid.UUID(target or product).hex,"message":{**message,"from":{"id":bot,"is_bot":True}}}}
	def event(value,secret=header):
		v={"run":c["run"],"brand":c["brand"],"channel":"telegram","origin":c["origins"]["telegram"],"actor":c["actor"],"event":str(value["update_id"]),"action":"telegram","body":f.canonical(value).decode(),"header":secret};raw=f.canonical(v);signature=hmac.new(bytes.fromhex(p.private(root/"inbox-ingress.private").strip()),raw,hashlib.sha256).hexdigest();return inbox.capture(root,raw,signature)
	def denied(fn):
		try: fn()
		except AssertionError: return
		raise AssertionError("Unsafe routing was accepted")
	def outcome(request):
		path=root/"target/results"/(request+".json");wait_for(path.is_file);return normalized(json.loads(path.read_text()))["outcome"]
	def route(value,expected):
		e=event(value)["event"];inbox.dispatch(root,e,"target",s["epoch"]);assert outcome(e)==expected;assert inbox.reconcile(root,e)["outcome"]=="telegram:"+expected;return e
	def direct(value,expected,secret=header):
		request=uuid.uuid4().hex;payload={"run":str(uuid.UUID(hex=m["run"])),"update":value["update_id"],"body":f.canonical(value).decode(),"header":secret};path=root/"target/telegram"/(request+".json");p.save(path,payload);os.chown(path,10001,10001);f.sync(path.parent);f.admit(root,"target",s["epoch"],"telegram",request);assert outcome(request)==expected
		p.save(root/("telegram-probe-"+request+".private.json"),{"request":request,"expected":expected,"update":value["update_id"],"payload_sha256":p.digest(path),"result_sha256":p.digest(root/"target/results"/(request+".json"))});return request
	try:
		before=snapshot(root);p.save(root/"telegram-before.private.json",f.sign(root,before));s=f.grant(root,"legacy",s["epoch"]);first=event(body(501,revision=1))["event"];denied(lambda:inbox.dispatch(root,first,"legacy",s["epoch"]));s=f.freeze(root,s["epoch"]);assert event(body(501,revision=1))["duplicate"] and f.stopped(m);denied(lambda:inbox.dispatch(root,first,"target",s["epoch"]));denied(lambda:event(body(506),"x"*43));record("authenticated-update-retained-across-legacy-freeze")
		s=f.grant(root,"target",s["epoch"]);denied(lambda:inbox.dispatch(root,first,"target",s["epoch"]-1))
		code="import os,signal,sys,time;from pathlib import Path;import cutover_inbox as i\nroot=Path(sys.argv[1]);i.dispatch(root,sys.argv[2],'target',int(sys.argv[3]));end=time.monotonic()+90\nwhile time.monotonic()<end:\n if (root/'target/results'/(sys.argv[2]+'.json')).is_file(): os.kill(os.getpid(),signal.SIGKILL)\n time.sleep(.1)\nraise SystemExit(124)\n"
		with (root/"telegram-coordinator-crash.private.log").open("wb") as log: crash=subprocess.run([sys.executable,"-c",code,str(root),first,str(s["epoch"])],env={**os.environ,"PYTHONPATH":str(root/"tools")},stdout=log,stderr=subprocess.STDOUT,timeout=120)
		(root/"telegram-coordinator-crash.exit").write_text(str(crash.returncode)+"\n");assert crash.returncode==-signal.SIGKILL and outcome(first)=="Product";assert inbox.reconcile(root,first)["outcome"]=="telegram:Product";direct(body(501,revision=1),"Duplicate");record("real-verifier-router-commit-survived-coordinator-sigkill")
		route(body(502,revision=1),"Rejected");route(body(503,actor=7999),"Rejected");route(body(504,bot=1002),"Rejected");route(body(505,target=str(uuid.uuid4())),"Rejected");direct(body(506),"Rejected",secret="x"*43);route(body(507,menu=True),"Menu");record("stale-foreign-actor-bot-product-and-secret-rejected")
		s=f.freeze(root,s["epoch"]);after=snapshot(root);changed={k for k,v in before["rows"]["target"].items() if after["rows"]["target"][k]!=v};assert changed<={"brands","users","bot_activity_events"} and {"brands","users","bot_activity_events"}<=changed;assert before["stock"]==after["stock"]=={"legacy":0,"target":0} and before["rows"]["legacy"]==after["rows"]["legacy"]==[1,1,1];p.save(root/"telegram-after.private.json",f.sign(root,after))
		s=f.grant(root,"target",s["epoch"]);direct(body(507,menu=True),"Duplicate");s=f.freeze(root,s["epoch"]);assert snapshot(root)==after;assert inbox.inventory(root)=={"result_observed":6};record("restart-replay-preserved-all-25-nonrouting-tables")
		time.sleep(.5);capture.terminate();capture.wait(timeout=10);stream.close();errors.close();running=set();starts=0;ids={m["containers"][side+"-db"]["id"] for side in f.SIDES}
		for line in (root/"telegram-docker-events.private.jsonl").read_text().splitlines():
			e=json.loads(line);id=e["Actor"]["ID"];assert id in ids
			if e["Action"]=="start": running.add(id);starts+=1;assert len(running)==1
			elif e["Action"]=="die": running.discard(id)
		assert starts==3 and not running and f.stopped(m);p.save(root/"telegram-runtime-result.private.json",{"run":m["run"],"phases":phases,"database_starts_observed":starts,"overlap":False,"coordinator_sigkill_exit":crash.returncode,"captured_router_events":6,"direct_replay_auth_probes":3,"unchanged_nonrouting_target_tables":25,"all_four_stopped":True,"actual_target_telegram_verifier_router_used":True,"real_telegram_transport_used":False,"payment_callback_routing_verified":False,"actual_shared_core_per_brand_lifecycle_verified":False,"protected_rehearsal_verified":False,"task_complete":False,"release_ready":False})
	finally:
		p.stop(root,m)
		if capture.poll() is None: capture.terminate();capture.wait(timeout=10)
		stream.close();errors.close()
if __name__=="__main__":
	import argparse
	a=argparse.ArgumentParser();a.add_argument("--root",required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
