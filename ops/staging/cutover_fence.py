import argparse,contextlib,fcntl,hashlib,hmac,json,os,re,stat,subprocess,time,uuid
from pathlib import Path
import cutover_pair as pair
if not __debug__: raise SystemExit(78)
SIDES={"legacy","target"}
def canonical(v): return json.dumps(v,sort_keys=True,separators=(",",":")).encode()
def digest(v): return hashlib.sha256(canonical(v)).hexdigest()
def model(root): return json.loads(pair.private(root/"pair.private.json"))
def owned(root,side,name):
	p=root/side/name;s=p.lstat()
	assert p.resolve(strict=True)==p and stat.S_ISREG(s.st_mode) and s.st_nlink==1 and s.st_uid==(1000 if side=="legacy" else 10001) and stat.S_IMODE(s.st_mode)==0o600
	return json.loads(p.read_text())
@contextlib.contextmanager
def lock(root):
	fd=os.open(root/"fence.lock",os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW,0o600)
	try:
		s=os.fstat(fd);assert s.st_uid==0 and s.st_nlink==1 and stat.S_IMODE(s.st_mode)==0o600
		fcntl.flock(fd,fcntl.LOCK_EX);yield
	finally: os.close(fd)
def key(root): return bytes.fromhex(pair.private(root/"fence-key.private").strip())
def sign(root,v): return {**v,"mac":hmac.new(key(root),canonical(v),hashlib.sha256).hexdigest()}
def verify(root,v):
	mac=v.pop("mac");assert hmac.compare_digest(mac,hmac.new(key(root),canonical(v),hashlib.sha256).hexdigest());return v
def header(root,m):
	h=json.loads(pair.private(root/"fence-header.private.json"))
	assert h["run"]==m["run"] and h["manifest"]==digest(m)
	for side in SIDES:
		assert owned(root,side,"stock-binding.json")==h["bindings"][side]
	return h
def chain(root,m,head=True):
	h=header(root,m);records=[];prev=digest(h)
	files=sorted((root/"fence-ledger").iterdir())
	assert files
	for n,f in enumerate(files):
		assert f.name==str(n).zfill(8)+".json"
		record=verify(root,json.loads(pair.private(f)))
		assert record["epoch"]==n and record["previous"]==prev and record["phase"] in {"frozen","granting","active"}
		assert record["owner"] in {None,*SIDES} and record["intent"] in {None,*SIDES} and (not records or not records[-1]["target_seen"] or record["target_seen"])
		prev=pair.digest(f);records.append(record)
	if head: assert json.loads(pair.private(root/"authority.private.json"))=={"epoch":records[-1]["epoch"],"hash":prev}
	return records
def append(root,m,phase,owner=None,intent=None,target_seen=False,stock=None,initial=False):
	if initial: epoch=0;prev=digest(header(root,m))
	else:
		old=chain(root,m);epoch=old[-1]["epoch"]+1;prev=pair.digest(root/"fence-ledger"/(str(epoch-1).zfill(8)+".json"));target_seen=target_seen or old[-1]["target_seen"]
	payload=sign(root,{"epoch":epoch,"previous":prev,"phase":phase,"owner":owner,"intent":intent,"target_seen":target_seen,"stock":stock})
	path=root/"fence-ledger"/(str(epoch).zfill(8)+".json");assert not path.exists();pair.save(path,payload);sync(path.parent)
	pair.save(root/"authority.private.json",{"epoch":epoch,"hash":pair.digest(path)});sync(root)
	return {k:v for k,v in payload.items() if k!="mac"}
def sync(path):
	fd=os.open(path,os.O_RDONLY|os.O_DIRECTORY)
	try: os.fsync(fd)
	finally: os.close(fd)
def stopped(m): return all(not pair.inspect(v["id"])["State"]["Running"] for v in m["containers"].values())
def quarantine(root):
	archive=root/"fence-quarantine";archive.mkdir(mode=0o700,exist_ok=True)
	for side in SIDES:
		for f in (root/side/"requests").iterdir():
			if not (root/side/"results"/(f.stem+".json")).exists(): os.rename(f,archive/(side+"-"+uuid.uuid4().hex+"-"+f.name))
	sync(archive)
def audit(root,m,h):
	assert stopped(m);pair.validate(root,m);observed={}
	for side in sorted(SIDES):
		b=h["bindings"][side];assert re.fullmatch("[a-f0-9]{64}",b["cipher"]) and b["plain"]==h["plain"]
		if side=="legacy":
			assert isinstance(b["unit"],int) and b["unit"]>0 and isinstance(b["product"],int) and b["product"]>0
			database="t068_legacy_stage";data="/var/lib/postgresql/data"
			sql="SELECT 'T068_STOCK|'||count(*)::text||'|'||count(*) FILTER(WHERE order_id IS NOT NULL)::text||'|'||count(*) FILTER(WHERE product_id="+str(b["product"])+" AND encode(sha256(convert_to(data,'UTF8')),'hex')='"+b["cipher"]+"')::text||'|'||current_setting('transaction_read_only') FROM inventory WHERE id="+str(b["unit"])+";"
		else:
			unit=str(uuid.UUID(b["unit"]));product=str(uuid.UUID(b["product"]));database=owned(root,side,"state.json")["database"];assert re.fullmatch("shop_qualification_[a-f0-9]{32}",database);data="/var/lib/postgresql/18/docker"
			sql="SELECT 'T068_STOCK|'||count(*)::text||'|'||count(*) FILTER(WHERE state='allocated')::text||'|'||count(*) FILTER(WHERE product_id='"+product+"'::uuid AND encode(sha256(credential_ciphertext),'hex')='"+b["cipher"]+"' AND encode(source_fingerprint,'hex')='"+h["plain"]+"' AND state IN ('available','allocated'))::text||'|'||current_setting('transaction_read_only') FROM shop.inventory_items WHERE id='"+unit+"'::uuid;"
		uid=70 if side=="legacy" else 999;mount="/var/lib/postgresql/data" if side=="legacy" else "/var/lib/postgresql";nonce=uuid.uuid4().hex
		args=["docker","run","--rm","-i","--name","teleg-t068-offline-"+nonce[:12],"--label","teleg.task=T068","--label","teleg.purpose=synthetic-offline-stock-audit","--user",str(uid)+":"+str(uid),"--network","none","--read-only","--cap-drop","ALL","--security-opt","no-new-privileges","--cpus",".25","--memory","96m","--memory-swap","96m","--pids-limit","64","--ulimit","fsize=67108864:67108864","--tmpfs","/tmp:rw,nosuid,size=16m","--mount","type=bind,source="+str(root/(side+"-db"))+",target="+mount,"--entrypoint","postgres",pair.PG[side],"--single","-D",data,"-c","shared_buffers=16MB","-c","default_transaction_read_only=on",database]
		query="BEGIN READ ONLY;\n"+sql+"\nROLLBACK;\n"
		result=subprocess.run(args,input=query.encode(),capture_output=True,timeout=60)
		log=root/("stock-audit-"+side+"-"+nonce+".private.log");log.write_bytes(result.stdout+result.stderr)
		matches=re.findall(rb'T068_STOCK\|(\d+)\|(\d+)\|(\d+)\|(on|off)',result.stdout)
		assert result.returncode==0 and len(matches)==1
		rows,used,valid,readonly=matches[0];assert rows==b"1" and valid==b"1" and readonly==b"on" and used in {b"0",b"1"}
		observed[side]={"rows":1,"used":int(used),"valid":True,"log_sha256":pair.digest(log),"query_sha256":hashlib.sha256(query.encode()).hexdigest()}
	assert stopped(m) and sum(v["used"] for v in observed.values())<=1
	return observed
def install(root):
	with lock(root):
		m=model(root);pair.validate(root,m);assert stopped(m) and not (root/"fence-header.private.json").exists()
		plans={s:owned(root,s,"stock-plan.json") for s in SIDES};assert plans["legacy"]==plans["target"] and uuid.UUID(plans["target"]["run"]).hex==m["run"]
		plain=hashlib.sha256(plans["target"]["credential"].encode()).hexdigest();bindings={s:owned(root,s,"stock-binding.json") for s in SIDES}
		h={"version":1,"run":m["run"],"manifest":digest(m),"plain":plain,"bindings":bindings}
		snapshot=audit(root,m,h);assert not any(v["used"] for v in snapshot.values())
		os.mkdir(root/"fence-ledger",0o700);os.mkdir(root/"fence-requests",0o700)
		fd=os.open(root/"fence-key.private",os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
		try: os.write(fd,os.urandom(32).hex().encode());os.fsync(fd)
		finally: os.close(fd)
		pair.save(root/"fence-header.private.json",h);sync(root)
		return append(root,m,"frozen",stock=snapshot,initial=True)
def freeze(root,epoch):
	with lock(root):
		m=model(root);s=chain(root,m)[-1];assert s["epoch"]==epoch
		pair.stop(root,m);quarantine(root);snapshot=audit(root,m,header(root,m))
		return append(root,m,"frozen",target_seen=s["target_seen"],stock=snapshot)
def grant(root,side,epoch):
	assert side in SIDES
	with lock(root):
		m=model(root);s=chain(root,m)[-1];assert s["epoch"]==epoch and s["phase"]=="frozen" and not(s["target_seen"] and side=="legacy")
		pair.validate(root,m);assert stopped(m)
		snapshot=audit(root,m,header(root,m));assert not snapshot["target" if side=="legacy" else "legacy"]["used"]
		quarantine(root);s=append(root,m,"granting",intent=side,target_seen=side=="target",stock=snapshot)
		permit=sign(root,{"epoch":s["epoch"],"side":side,"manifest":digest(m),"run":m["run"]});path=root/"start-permit.private.json"
		if path.exists(): path.rename(root/("stale-permit-"+uuid.uuid4().hex+".private.json"))
		pair.save(path,permit);sync(root)
		pair.start(root,m,side)
		assert all(not pair.inspect(m["containers"][r]["id"])["State"]["Running"] for r in [("target" if side=="legacy" else "legacy"),("target" if side=="legacy" else "legacy")+"-db"])
		return append(root,m,"active",owner=side,target_seen=s["target_seen"],stock=snapshot)
def consume_start(root,m,side):
	assert side in SIDES
	s=chain(root,m)[-1];p=root/"start-permit.private.json";v=verify(root,json.loads(pair.private(p)))
	assert s["phase"]=="granting" and s["intent"]==side and v=={"epoch":s["epoch"],"side":side,"manifest":digest(m),"run":m["run"]} and stopped(m)
	destination=root/("consumed-permit-"+str(s["epoch"])+".private.json");assert not destination.exists();p.rename(destination);sync(root)
def admit(root,side,epoch,command,request_id=None):
	assert side in SIDES and command in {"wallet","wallet-paused","telegram","status","fake_payment","fake_supplier","fake_telegram","fake_fx","fake_ai"} and (command!="telegram" or side=="target")
	with lock(root):
		m=model(root);s=chain(root,m)[-1];assert s["phase"]=="active" and s["owner"]==side and s["epoch"]==epoch
		pair.validate(root,m)
		assert pair.inspect(m["containers"][side+"-db"]["id"])["State"]["Running"]
		assert all(not pair.inspect(m["containers"][r]["id"])["State"]["Running"] for r in [("target" if side=="legacy" else "legacy"),("target" if side=="legacy" else "legacy")+"-db"])
		key=request_id or uuid.uuid4().hex;assert re.fullmatch("[a-f0-9]{32}",key)
		assert not (root/side/"step1-requests"/(key+".request")).exists()
		record=root/"fence-requests"/(key+".json");assert not record.exists()
		pair.save(record,sign(root,{"epoch":epoch,"side":side,"command":command,"request":key}));sync(record.parent)
		pair.request(root,side,command,key,_fenced=True,_wait=False)
		return {"epoch":epoch,"request":key}
def recover(root):
	with lock(root):
		m=model(root);pair.stop(root,m);quarantine(root);records=chain(root,m,head=False);last=records[-1]
		pair.save(root/"authority.private.json",{"epoch":last["epoch"],"hash":pair.digest(root/"fence-ledger"/(str(last["epoch"]).zfill(8)+".json"))});sync(root)
		snapshot=audit(root,m,header(root,m))
		return append(root,m,"frozen",target_seen=any(x["target_seen"] for x in records),stock=snapshot)
def main():
	os.umask(0o077);a=argparse.ArgumentParser();a.add_argument("action",choices=["install","grant","freeze","recover","status","admit"]);a.add_argument("--root",required=True);a.add_argument("--side",choices=sorted(SIDES));a.add_argument("--epoch",type=int);a.add_argument("--command");args=a.parse_args();root=Path(args.root)
	assert os.geteuid()==0 and re.fullmatch(r"/var/lib/teleg-cli/t068-pair-[a-z0-9-]+",str(root)) and root.resolve(strict=True)==root and stat.S_IMODE(root.stat().st_mode)==0o700
	if args.action=="install": result=install(root)
	elif args.action=="grant": result=grant(root,args.side,args.epoch)
	elif args.action=="freeze": result=freeze(root,args.epoch)
	elif args.action=="recover": result=recover(root)
	elif args.action=="admit": result=admit(root,args.side,args.epoch,args.command)
	else:
		with lock(root): result=chain(root,model(root))[-1]
	print(json.dumps({"epoch":result["epoch"],**({"request":result["request"]} if "request" in result else {"phase":result["phase"],"owner":result["owner"]})},sort_keys=True))
if __name__=="__main__":
	try: main()
	except Exception:
		print("Synthetic fence operation rejected.",file=__import__("sys").stderr);raise SystemExit(78)
