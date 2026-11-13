import hashlib,json,os,re,stat,subprocess,time,uuid
from pathlib import Path
import cutover_fence as f
p=f.pair
def key(root):
	m=f.model(root);v=f.verify(root,json.loads(p.private(root/"payment-binding.private.json")));k=f.owned(root,"target","payment-key.json");state=f.owned(root,"target","state.json")
	assert uuid.UUID(k["run"]).hex==m["run"] and k["member"]==state["user"] and k["origin"]=="topup:"+str(uuid.UUID(k["payment"])) and re.fullmatch("[a-f0-9]{64}",k["secret"]) and re.fullmatch("[0-9]{1,64}",k["provider"])
	assert v=={"run":m["run"],"manifest":f.digest(m),"key_sha256":p.digest(root/"target/payment-key.json")}
	for name in ["payment","payment-archives","payment-verification"]:
		path=root/"target"/name;s=path.lstat();assert path.resolve(strict=True)==path and stat.S_ISDIR(s.st_mode) and s.st_uid==10001 and stat.S_IMODE(s.st_mode)==0o700
	return k
def bind(root):
	from cutover_inbox import lock,config
	with lock(root),f.lock(root):
		config(root);m=f.model(root);s=f.chain(root,m)[-1];assert s["phase"]=="frozen" and f.stopped(m) and not (root/"payment-binding.private.json").exists()
		for name in ["payment-verification-history","payment-duplicates"]: (root/name).mkdir(mode=0o700)
		p.save(root/"payment-binding.private.json",f.sign(root,{"run":m["run"],"manifest":f.digest(m),"key_sha256":p.digest(root/"target/payment-key.json")}));f.sync(root);key(root)
def payload(v,nonce): return {"run":str(uuid.UUID(hex=v["run"])),"nonce":str(uuid.UUID(hex=nonce)),"event":v["event"],"body":v["body"],"signature":v["signature"]}
def verify(root,v):
	key(root);m=f.model(root);p.validate(root,m);available=int(next(line.split()[1] for line in Path('/proc/meminfo').read_text().splitlines() if line.startswith('MemAvailable:')))*1024;assert available>=(192+200)*1048576
	nonce=uuid.uuid4().hex;slot=root/"target/payment-verification";assert not list(slot.iterdir());history=root/"payment-verification-history"/nonce;history.mkdir(mode=0o700);input=slot/"input.json";p.save(input,payload(v,nonce));os.chown(input,10001,10001);f.sync(slot)
	name="teleg-t068-ipn-verify-"+nonce[:12];flags=["--name",name,"--label","teleg.task=T068","--label","teleg.purpose=fake-ipn-verification","--label","teleg.run="+m["run"],"--user","10001:10001","--network","none","--read-only","--cap-drop","ALL","--security-opt","no-new-privileges","--cpus",".1","--memory","192m","--memory-swap","192m","--pids-limit","64","--restart","no","--ulimit","fsize=67108864:67108864","--tmpfs","/tmp:rw,nosuid,size=64m","--tmpfs","/stage/target:rw,nosuid,size=1m,uid=10001,gid=10001,mode=0700","-e","SHOP_CUTOVER_STAGE=synthetic-acc-abad-v1","-e","SHOP_TEST_ALLOW_DATABASE_CREATION=1","-e","DOTNET_PROCESSOR_COUNT=1","-e","DOTNET_EnableDiagnostics=0","-e","DOTNET_CLI_TELEMETRY_OPTOUT=1","--workdir","/stage/target","--entrypoint","dotnet"]
	mounts=[(root/"target/identity","/stage/target/identity",False),(root/"target/payment-key.json","/stage/target/payment-key.json",False),(slot,"/stage/target/payment-verification",True),(root/"build/bin","/runtime",False)]
	for source,destination,rw in mounts: flags.extend(["--mount","type=bind,source="+str(source)+",target="+destination+("" if rw else ",readonly")])
	command=["/runtime/Platform.AotSmoke.dll","--cutover-stage","--root","/stage/target","--mode","payment-verify"];flags.extend([p.SDK,*command]);id=None;record=None
	try:
		id=p.call(["create",*flags]);x=p.inspect(id);h=x["HostConfig"];assert x["Image"]==p.SDK and x["Config"]["User"]=="10001:10001" and x["Config"]["Entrypoint"]==["dotnet"] and x["Config"]["Cmd"]==command and h["NetworkMode"]=="none" and not h["PortBindings"] and h["ReadonlyRootfs"] and not h["Privileged"] and h["Memory"]==h["MemorySwap"]==192*1048576 and h["PidsLimit"]==64 and h["CapDrop"]==["ALL"] and "no-new-privileges" in h["SecurityOpt"] and h["Ulimits"]==[{"Name":"fsize","Soft":67108864,"Hard":67108864}]
		assert sorted((q["Source"],q["Destination"],q["RW"]) for q in x["Mounts"])==sorted((str(a),b,c) for a,b,c in mounts)
		assert all(not e.startswith(("SHOP_TEST_POSTGRES_ADMIN=","POSTGRES_PASSWORD=")) for e in x["Config"]["Env"])
		p.save(history/"configuration.private.json",x);p.save(history/"runtime.private.json",{"id":id,"image":x["Image"],"run":m["run"],"artifact":f.digest(m["bundle"]),"key_sha256":p.digest(root/"target/payment-key.json"),"memory":h["Memory"],"network":"none","database_credentials_mounted":False,"command":command})
		with (history/"runtime.private.log").open("wb") as log: result=subprocess.run(["docker","start","--attach",id],stdout=log,stderr=subprocess.STDOUT,timeout=90)
		observed=p.inspect(id);p.save(history/"execution.private.json",{"id":id,"caller_exit":result.returncode,"container_exit":observed["State"]["ExitCode"],"oom_killed":observed["State"]["OOMKilled"],"started_at":observed["State"]["StartedAt"],"finished_at":observed["State"]["FinishedAt"]});assert result.returncode==observed["State"]["ExitCode"]==0 and not observed["State"]["OOMKilled"] and not observed["State"]["Running"]
		record=f.owned(root,"target","payment-verification/result.json");assert record["run"]==str(uuid.UUID(hex=m["run"])) and record["nonce"]==str(uuid.UUID(hex=nonce)) and record["event"]==v["event"] and record["bodySha256"]==hashlib.sha256(v["body"].encode()).hexdigest()
	finally:
		if id is not None:
			x=p.inspect(id);assert x["Config"]["Labels"]["teleg.run"]==m["run"] and x["Image"]==p.SDK and x["Name"]=="/"+name
			if x["State"]["Running"]: p.call(["stop","--time","5",id],15)
			p.call(["rm",id])
		for path in slot.iterdir():
			assert path.name in {"input.json","result.json"};path.rename(history/path.name)
		f.sync(history);f.sync(slot)
	assert record is not None and record["verified"] is True
	return {"nonce":nonce,"history":nonce,"result_sha256":p.digest(history/"result.json"),"execution_sha256":p.digest(history/"execution.private.json")}
def duplicate(root,event,value):
	path=root/"payment-duplicates"/event;path.mkdir(mode=0o700,exist_ok=True);digest=f.digest(value);destination=path/(digest+".json")
	if destination.exists(): assert f.verify(root,json.loads(p.private(destination)))==value
	else: p.save(destination,f.sign(root,value));f.sync(path);f.sync(path.parent)
