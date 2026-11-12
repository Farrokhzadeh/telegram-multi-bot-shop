import argparse,importlib.util,json,os,re,time,uuid
from pathlib import Path

if not __debug__: raise SystemExit(78)

spec=importlib.util.spec_from_file_location("pair",Path(__file__).with_name("cutover_pair.py"))
pair=importlib.util.module_from_spec(spec);spec.loader.exec_module(pair)

def snapshot(m,side):
	return {r:{k:pair.inspect(m["containers"][r]["id"])["State"][k] for k in ["Running","StartedAt"]} for r in [side,side+"-db"]}
def normalized(value): return {k.lower():v for k,v in value.items()}
def check(value,expected):
	value=normalized(value)
	assert value["synthetic"] is True and value["releaseready"] is False
	assert (value["orders"],value["allocated"],value["debits"])==(expected,expected,expected)
	return value
def run(root,resume=False,attempt="v2"):
	assert re.fullmatch("[a-z0-9-]+",attempt)
	m=json.loads(pair.private(root/"pair.private.json"));pair.validate(root,m)
	progress=root/("runtime-progress-"+attempt+".private.json" if resume else "runtime-progress.private.json")
	assert not progress.exists()
	prior=json.loads((root/"runtime-progress.private.json").read_text()) if resume else None
	if resume: assert prior["run"]==m["run"]
	done=[]
	def record(name,detail):
		done.append({"phase":name,"detail":detail});pair.save(progress,{"run":m["run"],"completed":done})
	for side in ["legacy","target"]:
		_,value=pair.request(root,side,"status");check(value,1 if resume else 0);record(side+"-ready",{"ready":True})
	wallets={}
	for side in ["legacy","target"]:
		key=next(x["detail"]["request"] for x in prior["completed"] if x["phase"]==side+"-wallet") if resume else None
		key,value=pair.request(root,side,"wallet",key);value=check(value,1)
		assert value["outcome"].lower() in ({"purchased","replayed","uncertain_do_not_retry"} if resume else {"purchased"})
		wallets[side]=key;record(side+"-wallet",{"request":key,"allocated":1,"debits":1})
	for side in ["legacy","target"]:
		for operation in ["fake_payment","fake_supplier","fake_telegram","fake_fx","fake_ai"]:
			_,value=pair.request(root,side,operation);value=check(value,1);assert value["outcome"]=="simulated"
		record(side+"-fake-transports",{"simulated":5,"domain_counts_unchanged":True})
	for side,other in [("legacy","target"),("target","legacy")]:
		before=snapshot(m,other)
		pair.stop(root,m,side)
		assert all(not v["Running"] for v in snapshot(m,side).values())
		assert snapshot(m,other)==before
		old=root/side/"results"/(wallets[side]+".json")
		archive=root/side/"prior-results";archive.mkdir(mode=0o700,exist_ok=True);old.rename(archive/(old.name+"."+uuid.uuid4().hex))
		pair.start(root,m,side)
		result=root/side/"results"/(wallets[side]+".json")
		for _ in range(120):
			if result.exists(): break
			time.sleep(.25)
		value=check(json.loads(result.read_text()),1)
		assert value["outcome"].lower()==("uncertain_do_not_retry" if side=="legacy" else "replayed")
		assert snapshot(m,other)==before
		record(side+"-independent-restart",{"counterpart_unchanged":True,"allocation_preserved":True,"replay":value["outcome"]})
	for side in ["legacy","target"]:
		key=uuid.uuid4().hex;bad=root/side/"requests"/(key+".request");os.mkfifo(bad,0o600);os.chown(bad,1000 if side=="legacy" else 10001,1000 if side=="legacy" else 10001)
		for _ in range(80):
			x=pair.inspect(m["containers"][side]["id"])
			if not x["State"]["Running"]: break
			time.sleep(.25)
		assert not x["State"]["Running"] and x["State"]["ExitCode"]==78 and not x["State"]["OOMKilled"]
		assert not (root/side/"results"/(key+".json")).exists()
		rejected=root/side/"rejected";rejected.mkdir(mode=0o700,exist_ok=True);bad.rename(rejected/bad.name)
		(root/(side+"-rejected.private.log")).write_text(pair.call(["logs","--tail","20",m["containers"][side]["id"]]))
		pair.stop(root,m,side);pair.start(root,m,side)
		_,value=pair.request(root,side,"status");check(value,1)
		record(side+"-malformed-request",{"exit":78,"no_result":True,"state_preserved":True})
	pair.validate(root,m)
	pair.stop(root,m)
	assert all(not pair.inspect(v["id"])["State"]["Running"] for v in m["containers"].values())
	pair.save(root/"runtime-result.private.json",{"run":m["run"],"phases":done,"step1_runtime_passed":True,"all_four_stopped":True,"protected_data_used":False,"cross_system_fencing_verified":False,"rollback_verified":False,"release_ready":False})
if __name__=="__main__":
	a=argparse.ArgumentParser();a.add_argument("--root",required=True);a.add_argument("--resume",action="store_true");a.add_argument("--attempt",default="v2");args=a.parse_args()
	root=Path(args.root)
	try: run(root,args.resume,args.attempt)
	except Exception:
		pair.stop(root,json.loads(pair.private(root/"pair.private.json")))
		raise
