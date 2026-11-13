import argparse,base64,contextlib,fcntl,hashlib,hmac,json,os,re,stat,uuid
from pathlib import Path
import cutover_fence as fence
pair=fence.pair
if not __debug__: raise SystemExit(78)
def directory(path):
	s=path.lstat();assert path.resolve(strict=True)==path and stat.S_ISDIR(s.st_mode) and s.st_uid==0 and stat.S_IMODE(s.st_mode)==0o700
@contextlib.contextmanager
def lock(root):
	directory(root);fd=os.open(root/"inbox.lock",os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW,0o600)
	try:
		s=os.fstat(fd);assert stat.S_ISREG(s.st_mode) and s.st_uid==0 and s.st_nlink==1 and stat.S_IMODE(s.st_mode)==0o600
		fcntl.flock(fd,fcntl.LOCK_EX);yield
	finally: os.close(fd)
def config(root):
	m=fence.model(root);fence.chain(root,m);c=fence.verify(root,json.loads(pair.private(root/"inbox.private.json")))
	assert c["version"]==1 and c["run"]==m["run"] and c["manifest"]==fence.digest(m) and c["brand"]=="acc-abad"
	directory(root/"inbox-events");return c
def install(root):
	with lock(root),fence.lock(root):
		m=fence.model(root);s=fence.chain(root,m)[-1];assert s["phase"]=="frozen" and fence.stopped(m)
		assert not (root/"inbox.private.json").exists() and not (root/"inbox-events").exists() and not (root/"inbox-ingress.private").exists()
		(root/"inbox-events").mkdir(mode=0o700)
		fd=os.open(root/"inbox-ingress.private",os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
		try: os.write(fd,os.urandom(32).hex().encode());os.fsync(fd)
		finally: os.close(fd)
		c={"version":1,"run":m["run"],"manifest":fence.digest(m),"brand":"acc-abad","origins":{k:uuid.uuid4().hex for k in ["telegram","payment"]},"actor":"synthetic-member"}
		pair.save(root/"inbox.private.json",fence.sign(root,c));fence.sync(root);return c
def identity(envelope): return fence.digest({k:envelope[k] for k in ["run","brand","channel","origin","event"]})[:32]
def telegram_payload(v): return {"run":str(uuid.UUID(hex=v["run"])),"update":int(v["event"]),"body":v["body"],"header":v["header"]}
def telegram_binding(root):
	m=fence.model(root);v=fence.verify(root,json.loads(pair.private(root/"telegram-binding.private.json")));key=fence.owned(root,"target","telegram-key.json");path=root/"target/telegram";s=path.lstat()
	assert path.resolve(strict=True)==path and stat.S_ISDIR(s.st_mode) and s.st_uid==10001 and stat.S_IMODE(s.st_mode)==0o700
	assert uuid.UUID(key["run"]).hex==m["run"] and re.fullmatch("[a-f0-9]{64}",key["secret"]) and v=={"run":m["run"],"manifest":fence.digest(m),"key_sha256":pair.digest(root/"target/telegram-key.json")}
	return key
def bind_telegram(root):
	with lock(root),fence.lock(root):
		config(root);m=fence.model(root);s=fence.chain(root,m)[-1];assert s["phase"]=="frozen" and fence.stopped(m) and not (root/"telegram-binding.private.json").exists()
		pair.save(root/"telegram-binding.private.json",fence.sign(root,{"run":m["run"],"manifest":fence.digest(m),"key_sha256":pair.digest(root/"target/telegram-key.json")}));fence.sync(root);telegram_binding(root)
def read(root,event):
	assert re.fullmatch("[a-f0-9]{32}",event);path=root/"inbox-events"/event;directory(path);records=[];previous=event
	for n,p in enumerate(sorted(path.iterdir())):
		assert p.name==str(n).zfill(8)+".json"
		v=fence.verify(root,json.loads(pair.private(p)))
		assert v["sequence"]==n and v["event"]==event and v["previous"]==previous
		if n==0: assert v["phase"]=="captured" and identity(v["envelope"])==event
		elif n==1: assert v["phase"] in {"dispatching","held"}
		else: assert n==2 and records[-1]["phase"]=="dispatching" and v["phase"] in {"result_observed","held"}
		previous=pair.digest(p);records.append(v)
	assert records;return records
def append(root,event,value):
	records=read(root,event);n=len(records);path=root/"inbox-events"/event
	v={"sequence":n,"event":event,"previous":pair.digest(path/(str(n-1).zfill(8)+".json")),**value};pair.save(path/(str(n).zfill(8)+".json"),fence.sign(root,v));fence.sync(path);return v
def decode(body):
	assert isinstance(body,bytes) and 2<=len(body)<=4096
	def unique(pairs):
		v={}
		for k,x in pairs:
			assert k not in v;v[k]=x
		return v
	v=json.loads(body,object_pairs_hook=unique);assert isinstance(v,dict) and all(isinstance(x,str) for x in v.values());return v
def capture(root,body,signature):
	with lock(root):
		assert isinstance(body,bytes) and 2<=len(body)<=4096
		c=config(root);key=bytes.fromhex(pair.private(root/"inbox-ingress.private").strip())
		assert isinstance(signature,str) and re.fullmatch("[a-f0-9]{64}",signature) and hmac.compare_digest(signature,hmac.new(key,body,hashlib.sha256).hexdigest())
		v=decode(body);assert body==fence.canonical(v) and set(v)=={"run","brand","channel","origin","actor","event","action"}|({"body","header"} if v.get("action")=="telegram" else {"body","signature"} if v.get("action") in {"payment","legacy-payment"} else set())
		assert v["run"]==c["run"] and v["brand"]==c["brand"] and v["actor"]==c["actor"] and v["channel"] in c["origins"] and v["origin"]==c["origins"][v["channel"]]
		verification=None
		if v["action"] in {"payment","legacy-payment"}:
			assert v["channel"]=="payment" and re.fullmatch("[a-f0-9]{64}",v["event"]) and re.fullmatch("[a-fA-F0-9]{128}",v["signature"]) and v["event"]==hashlib.sha256(bytes.fromhex(v["signature"])).hexdigest() and 2<=len(v["body"].encode())<=2048
			if v["action"]=="legacy-payment":
				from cutover_legacy_inbox import verify
			else:
				from cutover_payment import verify
			verification=verify(root,v)
		else: assert re.fullmatch("0|[1-9][0-9]{0,18}",v["event"]) and int(v["event"])<=9223372036854775807 and v["action"] in ({"wallet","wallet-paused","telegram"} if v["channel"]=="telegram" else {"review"})
		if v["action"]=="telegram":
			key=telegram_binding(root);expected=base64.urlsafe_b64encode(bytes.fromhex(key["secret"])).decode().rstrip("=");assert hmac.compare_digest(v["header"],expected) and 2<=len(v["body"].encode())<=2048
			update=json.loads(v["body"]);assert isinstance(update,dict) and type(update.get("update_id")) is int and update["update_id"]==int(v["event"])
		event=identity(v);path=root/"inbox-events"/event;value={"sequence":0,"event":event,"previous":event,"phase":"captured","envelope":v,"body_sha256":hashlib.sha256(body).hexdigest(),"signature":signature}
		if verification is not None: value["verification"]=verification
		if path.exists():
			prior=read(root,event)[0]
			if verification is not None:
				assert {k:x for k,x in prior["envelope"].items() if k not in {"body","signature"}}=={k:x for k,x in v.items() if k not in {"body","signature"}}
				if prior["envelope"]!=v:
					from cutover_payment import duplicate
					duplicate(root,event,value)
			else: assert prior==value
			fence.sync(path);fence.sync(path.parent);return {"event":event,"duplicate":True}
		temp=root/"inbox-events"/("pending-"+uuid.uuid4().hex);temp.mkdir(mode=0o700)
		pair.save(temp/"00000000.json",fence.sign(root,value));fence.sync(temp);temp.rename(path);fence.sync(path.parent)
		return {"event":event,"duplicate":False}
def dispatch(root,event,side,epoch):
	with lock(root):
		config(root);records=read(root,event);assert len(records)==1
		if records[0]["envelope"]["channel"]=="payment" and records[0]["envelope"]["action"]=="review": return append(root,event,{"phase":"held","reason":"payment_requires_reconciliation"})
		assert side in fence.SIDES
		envelope=records[0]["envelope"]
		assert envelope["action"]!="legacy-payment"
		if envelope["action"]=="telegram": assert side=="target";telegram_binding(root)
		if envelope["action"]=="payment":
			from cutover_payment import key
			assert side=="target";key(root)
		with fence.lock(root):
			m=fence.model(root);s=fence.chain(root,m)[-1];assert s["phase"]=="active" and s["owner"]==side and s["epoch"]==epoch
		append(root,event,{"phase":"dispatching","side":side,"epoch":epoch,"request":event})
		if envelope["action"]=="telegram":
			path=root/"target/telegram"/(event+".json");assert not path.exists();pair.save(path,telegram_payload(envelope));os.chown(path,10001,10001);fence.sync(path.parent)
		if envelope["action"]=="payment":
			from cutover_payment import payload
			path=root/"target/payment"/(event+".json");assert not path.exists();pair.save(path,payload(envelope,records[0]["verification"]["nonce"]));os.chown(path,10001,10001);fence.sync(path.parent)
		fence.admit(root,side,epoch,records[0]["envelope"]["action"],event)
		return {"event":event,"phase":"dispatching"}
def reconcile(root,event):
	with lock(root):
		config(root);records=read(root,event);last=records[-1]
		if last["phase"]!="dispatching": return {"event":event,"phase":last["phase"]}
		path=root/last["side"]/"results"/(event+".json")
		if not path.exists(): return {"event":event,"phase":"uncertain_do_not_retry"}
		admission=fence.verify(root,json.loads(pair.private(root/"fence-requests"/(event+".json"))))
		assert admission=={"epoch":last["epoch"],"side":last["side"],"command":records[0]["envelope"]["action"],"request":event}
		v={k.lower():x for k,x in fence.owned(root,last["side"],"results/"+event+".json").items()}
		command=records[0]["envelope"]["action"];assert v["operation"]==(command if command in {"telegram","payment"} else "wallet") and v["synthetic"] is True and v["releaseready"] is False
		if command=="payment":
			from cutover_payment import payload
			assert fence.owned(root,"target","payment/"+event+".json")==payload(records[0]["envelope"],records[0]["verification"]["nonce"])
			if v["outcome"] not in {"Pending","Ready","Review","Replayed","Held","Rejected"}: return {"event":event,"phase":"uncertain_do_not_retry"}
			return append(root,event,{"phase":"held" if v["outcome"]=="Held" else "result_observed","side":last["side"],"epoch":last["epoch"],"result_sha256":pair.digest(path),"outcome":"payment:"+v["outcome"],**({"reason":"authentic_unbound_payment"} if v["outcome"]=="Held" else {})})
		if command=="telegram":
			assert fence.owned(root,"target","telegram/"+event+".json")==telegram_payload(records[0]["envelope"])
			if v["outcome"] not in {"Product","Menu","Duplicate","Rejected"}: return {"event":event,"phase":"uncertain_do_not_retry"}
			return append(root,event,{"phase":"result_observed","side":last["side"],"epoch":last["epoch"],"result_sha256":pair.digest(path),"outcome":"telegram:"+v["outcome"]})
		if v["outcome"] not in {"Purchased","purchased"}: return {"event":event,"phase":"uncertain_do_not_retry"}
		return append(root,event,{"phase":"result_observed","side":last["side"],"epoch":last["epoch"],"result_sha256":pair.digest(path),"outcome":"purchased"})
def inventory(root):
	with lock(root):
		config(root);counts={}
		for p in (root/"inbox-events").iterdir():
			records=read(root,p.name);phase=records[-1]["phase"];counts[phase]=counts.get(phase,0)+1
		return counts
def main():
	os.umask(0o077);a=argparse.ArgumentParser();a.add_argument("action",choices=["install","bind-telegram","bind-payment","capture","dispatch","reconcile","status"]);a.add_argument("--root",required=True);a.add_argument("--event");a.add_argument("--side",choices=sorted(fence.SIDES));a.add_argument("--epoch",type=int);args=a.parse_args();root=Path(args.root)
	assert os.geteuid()==0 and re.fullmatch(r"/var/lib/teleg-cli/t068-pair-[a-z0-9-]+",str(root));directory(root)
	if args.action=="install": install(root);result={"installed":True}
	elif args.action=="bind-telegram": bind_telegram(root);result={"bound":True}
	elif args.action=="bind-payment":
		from cutover_payment import bind
		bind(root);result={"bound":True}
	elif args.action=="capture":
		v=decode(__import__("sys").stdin.buffer.read(8193));assert set(v)=={"body","signature"};result=capture(root,v["body"].encode(),v["signature"])
	elif args.action=="dispatch": result=dispatch(root,args.event,args.side,args.epoch)
	elif args.action=="reconcile": result=reconcile(root,args.event)
	else: result=inventory(root)
	print(json.dumps(result,sort_keys=True))
if __name__=="__main__":
	try: main()
	except Exception:
		print("Synthetic inbox operation rejected.",file=__import__("sys").stderr);raise SystemExit(78)
