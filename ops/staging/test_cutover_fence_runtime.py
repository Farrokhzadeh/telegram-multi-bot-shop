import concurrent.futures,importlib.util,json,os,subprocess,sys,time,uuid
from pathlib import Path
import cutover_fence as f
p=f.pair
KINDS=["bot","callback","admin","job","import","stock"]
def normalized(v): return {k.lower():x for k,x in v.items()}
def wait_for(fn,seconds=90):
	end=time.monotonic()+seconds
	while time.monotonic()<end:
		value=fn()
		if value: return value
		time.sleep(.2)
	raise ValueError("Bounded observation timed out")
def run(root):
	m=f.model(root);f.chain(root,m);assert f.stopped(m);began=int(time.time());events=[];actors=[]
	stream=(root/"docker-events.private.jsonl").open("wb");errors=(root/"docker-events-errors.private.log").open("wb")
	capture=subprocess.Popen(["docker","events","--since",str(began),"--filter","container="+m["containers"]["legacy-db"]["id"],"--filter","container="+m["containers"]["target-db"]["id"],"--filter","event=start","--filter","event=die","--filter","event=destroy","--format","{{json .}}"],stdout=stream,stderr=errors)
	time.sleep(.2);assert capture.poll() is None
	def record(name,detail):
		events.append({"phase":name,"detail":detail});p.save(root/"fence-runtime-progress.private.json",{"run":m["run"],"completed":events})
	def cli(*args):
		return subprocess.run([sys.executable,str(root/"tools/cutover_fence.py"),*map(str,args),"--root",str(root)],capture_output=True,timeout=180)
	def state(): return f.chain(root,m)[-1]
	def denied(*args): assert cli(*args).returncode==78
	def check_owner(side):
		for role,v in m["containers"].items(): assert p.inspect(v["id"])["State"]["Running"]==(role in {side,side+"-db"})
	def actor(side,kind):
		uid=70 if side=="legacy" else 999;name="teleg-t068-fence-probe-"+uuid.uuid4().hex[:12];mount="/var/lib/postgresql/data" if side=="legacy" else "/var/lib/postgresql"
		id=p.call(["run","--rm","-d","--name",name,"--label","teleg.task=T068","--label","teleg.purpose=fence-writer-probe","--label","teleg.run="+m["run"],"--user",str(uid)+":"+str(uid),"--network","container:"+m["containers"][side+"-db"]["id"],"--read-only","--cap-drop","ALL","--security-opt","no-new-privileges","--memory","32m","--memory-swap","32m","--cpus",".1","--pids-limit","32","--tmpfs",mount+":rw,nosuid,size=8m","--tmpfs","/tmp:rw,nosuid,size=8m","--env-file",str(root/(side+"-db.env")),"-e","PGAPPNAME=t068-probe-"+kind,"--entrypoint","sleep",p.PG[side],"1200"])
		x=p.inspect(id);assert x["Image"]==p.PG[side] and x["Config"]["Labels"]["teleg.run"]==m["run"] and not x["Mounts"] and x["State"]["Running"]
		actors.append(id);return id
	def sql(id,side,query,timeout=15):
		assert p.inspect(id)["State"]["Running"]
		database="t068_legacy_stage" if side=="legacy" else f.owned(root,side,"state.json")["database"]
		command='export PGPASSWORD="$POSTGRES_PASSWORD"; exec psql -X -w -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -U "$POSTGRES_USER" -d "$1" -Atc "$2"'
		return subprocess.run(["docker","exec",id,"sh","-c",command,"probe",database,query],capture_output=True,timeout=timeout)
	def writers(side):
		b=f.header(root,m)["bindings"][side];query="BEGIN; UPDATE products SET stock=stock WHERE id="+str(b["product"])+";ROLLBACK;" if side=="legacy" else "BEGIN;UPDATE shop.products SET title=title WHERE id='"+b["product"]+"'::uuid;ROLLBACK;"
		clients=[actor(side,k) for k in KINDS]
		for id in clients: assert sql(id,side,query).returncode==0
		return clients,query
	def blocked(clients,side,query):
		for id in clients:
			r=sql(id,side,query);assert r.returncode!=0 and b"password authentication failed" not in r.stderr
		for id in clients:
			x=p.inspect(id);assert x["Config"]["Labels"]["teleg.run"]==m["run"];p.call(["stop","--time","5",id]);actors.remove(id)
	try:
		root_arg=["--root",str(root)]
		r=subprocess.run([sys.executable,str(root/"tools/cutover_pair.py"),"start",*root_arg,"--side","legacy"],capture_output=True,timeout=20);assert r.returncode!=0 and f.stopped(m)
		s=f.grant(root,"legacy",state()["epoch"]);check_owner("legacy");record("legacy-owner",{"epoch":s["epoch"]})
		denied("admit","--side","target","--epoch",s["epoch"],"--command","wallet");denied("admit","--side","legacy","--epoch",s["epoch"]-1,"--command","wallet")
		clients,query=writers("legacy");record("legacy-six-writers-enabled",{"writer_classes":6})
		held=f.admit(root,"legacy",s["epoch"],"wallet-paused")["request"]
		wait_for(lambda:(root/"legacy"/("held-"+held+".json")).is_file())
		observe=sql(clients[0],"legacy","SELECT count(*) FROM pg_stat_activity WHERE application_name='t068-cutover-stage' AND state='idle in transaction' AND xact_start IS NOT NULL;")
		assert observe.returncode==0 and int(observe.stdout.strip())==1
		s=f.freeze(root,s["epoch"]);assert f.stopped(m) and s["stock"]["legacy"]["used"]==0
		blocked(clients,"legacy",query);assert not (root/"legacy/results"/(held+".json")).exists()
		assert any(held in x.name for x in (root/"fence-quarantine").iterdir())
		record("stale-legacy-writers-fenced",{"writer_classes":6,"uncommitted_checkout_rolled_back":True,"pending_input_retained":True})
		epoch=s["epoch"]
		with concurrent.futures.ThreadPoolExecutor(2) as pool:
			results=list(pool.map(lambda side:cli("grant","--side",side,"--epoch",epoch),["legacy","target"]))
		assert sorted(x.returncode for x in results)==[0,78]
		winner=state()["owner"];check_owner(winner);s=f.freeze(root,state()["epoch"]);record("competing-grants",{"successful":1,"rejected":1})
		crash_code="import os,signal,sys;from pathlib import Path;from unittest.mock import patch;import cutover_fence as f\nroot=Path(sys.argv[1]);save=f.pair.save\ndef cut(path,value):\n if path==root/'authority.private.json': os.kill(os.getpid(),signal.SIGKILL)\n return save(path,value)\nwith patch.object(f.pair,'save',side_effect=cut): f.grant(root,'target',int(sys.argv[2]))\n"
		with (root/"coordinator-crash.private.log").open("wb") as crash_log:
			crash=subprocess.run([sys.executable,"-c",crash_code,str(root),str(s["epoch"])],env={**os.environ,"PYTHONPATH":str(root/"tools")},stdout=crash_log,stderr=subprocess.STDOUT,timeout=180)
		(root/"coordinator-crash.exit").write_text(str(crash.returncode)+"\n");assert crash.returncode==-9 and f.stopped(m)
		denied("grant","--side","target","--epoch",s["epoch"])
		s=f.recover(root);assert f.stopped(m) and s["owner"] is None and s["target_seen"]
		denied("grant","--side","legacy","--epoch",s["epoch"])
		record("interrupted-intent-recovered",{"coordinator_sigkill":True,"old_head_rejected":True,"recovery_left_both_frozen":True,"legacy_resume_rejected":True})
		s=f.grant(root,"target",s["epoch"]);check_owner("target");clients,query=writers("target")
		denied("admit","--side","target","--epoch",s["epoch"]-1,"--command","wallet")
		request=f.admit(root,"target",s["epoch"],"wallet")["request"]
		path=root/"target/results"/(request+".json");wait_for(path.is_file);result=normalized(json.loads(path.read_text()));assert result["outcome"]=="Purchased"
		s=f.freeze(root,s["epoch"]);assert s["stock"]["legacy"]["used"]==0 and s["stock"]["target"]["used"]==1
		blocked(clients,"target",query);denied("grant","--side","legacy","--epoch",s["epoch"])
		record("common-unit-allocated-once",{"legacy":0,"target":1,"target_writer_classes_fenced_after_freeze":6,"reverse_activation_rejected":True})
		time.sleep(.5);capture.terminate();capture.wait(timeout=10);stream.close();errors.close();raw=(root/"docker-events.private.jsonl").read_text();running=set();ids={m["containers"][s+"-db"]["id"] for s in f.SIDES};starts=0
		for line in raw.splitlines():
			e=json.loads(line);id=e.get("Actor",{}).get("ID");action=e.get("Action")
			if id not in ids: continue
			if action=="start": running.add(id);starts+=1;assert len(running)<=1
			elif action in {"die","destroy"}: running.discard(id)
		assert starts>=3 and not running and f.stopped(m)
		p.save(root/"fence-runtime-result.private.json",{"run":m["run"],"phases":events,"database_starts_observed":starts,"overlapping_writer_databases":False,"common_unit_legacy_used":0,"common_unit_target_used":1,"all_four_stopped":True,"task_complete":False,"callback_handoff_verified":False,"cutover_rollback_verified":False,"release_ready":False})
	finally:
		p.stop(root,m)
		if capture.poll() is None: capture.terminate();capture.wait(timeout=10)
		stream.close();errors.close()
		for id in actors:
			x=p.inspect(id);assert x["Config"]["Labels"]["teleg.run"]==m["run"];p.call(["stop","--time","5",id])
if __name__=="__main__":
	import argparse
	a=argparse.ArgumentParser();a.add_argument("--root",required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
