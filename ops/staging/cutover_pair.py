import argparse,base64,fcntl,shutil,hashlib,json,os,re,secrets,stat,subprocess,sys,time,uuid
from pathlib import Path

if not __debug__: raise SystemExit(78)

SDK="sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e"
LEGACY="sha256:77523faca5ee34d640cb98b9132ab8f649b0adf95aa104d1a974308fef8fc23d"
PG={"legacy":"sha256:e013e867e712fec275706a6c51c966f0bb0c93cfa8f51000f85a15f9865a28cb","target":"sha256:bd5dfa494a5a259704365813dbfa5fa6b3e69be7348d43b547f540c730478583"}
CAPS={"legacy-db":96,"target-db":96,"legacy":160,"target":640}

HBA="local all all scram-sha-256\nhost all all 127.0.0.1/32 scram-sha-256\nhost all all ::1/128 scram-sha-256\nlocal replication all reject\nhost replication all all reject\nhost all all all reject\n"
def hba_path(root,side): return root/(side+"-db")/("pg_hba.conf" if side=="legacy" else "18/docker/pg_hba.conf")
def seal_auth(root,side):
	path=hba_path(root,side);uid=70 if side=="legacy" else 999;s=path.lstat()
	assert path.resolve(strict=True)==path and stat.S_ISREG(s.st_mode) and s.st_nlink==1 and s.st_uid==uid and stat.S_IMODE(s.st_mode)==0o600
	if path.read_text()==HBA: return
	backup=root/(side+"-hba-before-"+uuid.uuid4().hex+".private.json");save(backup,{"policy":path.read_text()})
	temp=path.with_name("pg_hba.conf.t068-"+uuid.uuid4().hex)
	fd=os.open(temp,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
	try: os.write(fd,HBA.encode());os.fchown(fd,uid,uid);os.fsync(fd)
	finally: os.close(fd)
	os.replace(temp,path)
def verify_auth(root,m,side):
	values=dict(line.split("=",1) for line in private(root/(side+"-db.env")).splitlines())
	def sql(password,query):
		command='IFS= read -r PGPASSWORD; export PGPASSWORD; exec psql -X -w -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Atc "$1"'
		return subprocess.run(["docker","exec","-i",m["containers"][side+"-db"]["id"],"/bin/sh","-c",command,"guard",query],input=(password+"\n").encode(),capture_output=True,timeout=15)
	assert sql(values["POSTGRES_PASSWORD"],"SELECT pg_reload_conf()").returncode==0
	for _ in range(20):
		bad=sql("synthetic-invalid-password","SELECT 1")
		if bad.returncode!=0: break
		time.sleep(.1)
	else: raise ValueError()
	good=sql(values["POSTGRES_PASSWORD"],"SELECT 1");assert good.returncode==0 and good.stdout.strip()==b"1"
	actual=sql(values["POSTGRES_PASSWORD"],"SHOW hba_file")
	expected="/var/lib/postgresql/data/pg_hba.conf" if side=="legacy" else "/var/lib/postgresql/18/docker/pg_hba.conf"
	assert actual.returncode==0 and actual.stdout.decode().strip()==expected
	save(root/("authentication-"+side+"-"+uuid.uuid4().hex+".private.json"),{"side":side,"container":m["containers"][side+"-db"]["id"],"policy_sha256":hashlib.sha256(HBA.encode()).hexdigest(),"correct_password_accepted":True,"wrong_password_rejected":True})

def call(args,timeout=30):
	return subprocess.check_output(["docker",*args],stderr=subprocess.PIPE,timeout=timeout).decode().strip()

def inspect(name): return json.loads(call(["inspect",name]))[0]
def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def private(path):
	s=path.lstat()
	if not stat.S_ISREG(s.st_mode) or s.st_nlink!=1 or s.st_uid!=0 or stat.S_IMODE(s.st_mode)&0o077: raise ValueError()
	return path.read_text()
def save(path,value):
	temp=path.with_name(path.name+".tmp")
	fd=os.open(temp,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
	try: os.write(fd,(json.dumps(value,sort_keys=True)+"\n").encode());os.fsync(fd)
	finally: os.close(fd)
	os.replace(temp,path)
def env(path,values):
	fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
	try: os.write(fd,("".join(k+"="+v+"\n" for k,v in values.items())).encode())
	finally: os.close(fd)
def directory(path,uid=0):
	path.mkdir(mode=0o700);os.chown(path,uid,uid)
def config(root,run,role):
	prefix="teleg-t068-pair-"+run[:8]+"-"
	name=prefix+role
	uid=70 if role=="legacy-db" else 999 if role=="target-db" else 1000 if role=="legacy" else 10001
	memory=CAPS[role]*1048576
	flags=["--name",name,"--label","teleg.task=T068","--label","teleg.purpose=synthetic-cutover-step1","--label","teleg.run="+run,"--label","teleg.role="+role,"--user",str(uid)+":"+str(uid),"--read-only","--cap-drop","ALL","--security-opt","no-new-privileges","--cpus","0.1" if role.endswith("-db") else "0.25","--memory",str(memory),"--memory-swap",str(memory),"--pids-limit","96","--restart","no","--log-opt","max-size=2m","--log-opt","max-file=2","--tmpfs","/tmp:rw,nosuid,size=64m","--ulimit","fsize=67108864:67108864"]
	if role.endswith("-db"):
		side=role[:-3];destination="/var/lib/postgresql/data" if side=="legacy" else "/var/lib/postgresql"
		mounts=[(str(root/role),destination,True)]
		flags+=["--network","none","--tmpfs","/var/run/postgresql:rw,nosuid,size=8m,uid="+str(uid)+",gid="+str(uid),"--env-file",str(root/(role+".env")),"--mount","type=bind,source="+str(root/role)+",target="+destination,PG[side],"-c","shared_buffers=16MB","-c","max_connections=16","-c","work_mem=1MB","-c","maintenance_work_mem=16MB"]
		image=PG[side]
	else:
		mounts=[(str(root/role),"/stage/"+role,True)]
		flags+=["--network","container:"+prefix+role+"-db","--env-file",str(root/(role+".env")),"--mount","type=bind,source="+str(root/role)+",target=/stage/"+role]
		if role=="legacy":
			mounts.append((str(root/"tools/cutover_legacy_runtime.py"),"/runtime/legacy.py",False))
			flags+=["--mount","type=bind,source="+str(root/"tools/cutover_legacy_runtime.py")+",target=/runtime/legacy.py,readonly","--workdir","/app","--entrypoint","python3",LEGACY,"/runtime/legacy.py","serve"]
			image=LEGACY
		else:
			mounts.append((str(root/"build/bin"),"/runtime",False))
			flags+=["--mount","type=bind,source="+str(root/"build/bin")+",target=/runtime,readonly","--workdir","/stage/target","--entrypoint","dotnet",SDK,"/runtime/Platform.AotSmoke.dll","--cutover-stage","--root","/stage/target","--mode","serve"]
			image=SDK
	return name,image,uid,memory,mounts,flags
def validate(root,m):
	for role,saved in m["containers"].items():
		name,image,uid,memory,mounts,_=config(root,m["run"],role)
		x=inspect(saved["id"]);h=x["HostConfig"]
		assert x["Id"]==saved["id"] and x["Name"]=="/"+name and x["Image"]==image and x["Config"]["User"]==str(uid)+":"+str(uid)
		assert x["Config"]["Labels"]["teleg.run"]==m["run"] and x["Config"]["Labels"]["teleg.task"]=="T068" and x["Config"]["Labels"]["teleg.role"]==role
		assert h["ReadonlyRootfs"] and not h["Privileged"] and not h["PortBindings"] and h["CapDrop"]==["ALL"] and h["Memory"]==memory and h["MemorySwap"]==memory and h["PidsLimit"]==96 and h["RestartPolicy"]["Name"]=="no"
		assert "no-new-privileges" in h["SecurityOpt"] and h["Ulimits"]==[{"Name":"fsize","Hard":67108864,"Soft":67108864}]
		assert sorted((v["Source"],v["Destination"],v["RW"]) for v in x["Mounts"])==sorted(mounts)
		for source,_,_ in mounts:
			assert Path(source).resolve(strict=True)==Path(source) and str(Path(source)).startswith(str(root)+"/")
		if role.endswith("-db") and hba_path(root,role[:-3]).exists():
			file=hba_path(root,role[:-3]);st=file.lstat();assert file.resolve(strict=True)==file and st.st_uid==uid and st.st_nlink==1 and stat.S_IMODE(st.st_mode)==0o600 and file.read_text()==HBA
		expected=dict(line.split("=",1) for line in private(root/(role+".env")).splitlines())
		actual=dict(line.split("=",1) for line in x["Config"]["Env"])
		assert all(actual.get(k)==v for k,v in expected.items())
		if role.endswith("-db"):
			assert set(x["NetworkSettings"]["Networks"])=={"none"} and x["Config"]["Cmd"]==["-c","shared_buffers=16MB","-c","max_connections=16","-c","work_mem=1MB","-c","maintenance_work_mem=16MB"]
		else:
			assert x["Config"]["Entrypoint"]==(["python3"] if role=="legacy" else ["dotnet"])
			assert x["Config"]["Cmd"]==(["/runtime/legacy.py","serve"] if role=="legacy" else ["/runtime/Platform.AotSmoke.dll","--cutover-stage","--root","/stage/target","--mode","serve"])
		for source,_,rw in mounts:
			if rw: assert Path(source).stat().st_uid==uid and stat.S_IMODE(Path(source).stat().st_mode)==0o700
		mode=h["NetworkMode"]
		if role.endswith("-db"): assert mode=="none"
		else: assert mode in {"container:"+config(root,m["run"],role+"-db")[0],"container:"+m["containers"][role+"-db"]["id"]}
	assert {str(f.relative_to(root/"build/bin")):digest(f) for f in (root/"build/bin").rglob("*") if f.is_file()}==m["bundle"]
	assert digest(root/"tools/cutover_legacy_runtime.py")==m["legacy_script"]
def prepare(root):
	assert not (root/"pair.private.json").exists()
	mem=int(next(line.split()[1] for line in Path("/proc/meminfo").read_text().splitlines() if line.startswith("MemAvailable:")))*1024
	assert mem>=(sum(CAPS.values())+200)*1048576
	for image in [SDK,LEGACY,*PG.values()]:
		x=json.loads(call(["image","inspect",image]))[0]
		assert x["Id"]==image
		for entry in x["Config"].get("Env",[]):
			k,_,v=entry.partition("=")
			assert not (k in {"BOT_TOKEN","NP_API_KEY","NP_IPN_SECRET","FIELD_ENCRYPTION_KEY","POSTGRES_PASSWORD"} and v)
	run=uuid.uuid4().hex
	for side,uid in [("legacy",1000),("target",10001)]:
		directory(root/side,uid)
		for part in ["requests","results","attempts","tmp","logs"]: directory(root/side/part,uid)
		p=root/side/"identity";p.write_text(run+"\n");p.chmod(0o600);os.chown(p,uid,uid)
	for side,uid in [("legacy",70),("target",999)]:
		directory(root/(side+"-db"),uid)
		password=secrets.token_hex(24)
		values={"POSTGRES_USER":"t068_legacy_owner" if side=="legacy" else "shop_test_runner","POSTGRES_DB":"t068_legacy_stage" if side=="legacy" else "shop_test_control","POSTGRES_PASSWORD":password}
		env(root/(side+"-db.env"),values)
		if side=="legacy":
			values|={"POSTGRES_HOST":"127.0.0.1","POSTGRES_PORT":"5432","DJANGO_SETTINGS_MODULE":"django_project.settings.base","DJANGO_SECRET_KEY":secrets.token_hex(32),"FIELD_ENCRYPTION_KEY":base64.urlsafe_b64encode(secrets.token_bytes(32)).decode(),"INSTANCE_ID":"t068-acc-abad","DEPLOYMENT_STAGE":"staging","INSTANCE_BINDING_REQUIRED":"1","SHOP_CUTOVER_STAGE":"synthetic-acc-abad-v1","PANEL_LOG_DIR":"/stage/legacy/logs","PANEL_LOG_MAX_BYTES":"1048576","PANEL_LOG_BACKUPS":"1","BOT_TOKEN":"","BOT_STATUS":"0","NP_API_KEY":"","NP_IPN_SECRET":"","ADMIN_IDS":"","AI_WORKER_TOKEN":"","AI_WORKER_URL":"","PYTHONDONTWRITEBYTECODE":"1","PYTHONPATH":"/app"}
		else:
			values={"SHOP_CUTOVER_STAGE":"synthetic-acc-abad-v1","SHOP_TEST_ALLOW_DATABASE_CREATION":"1","SHOP_TEST_POSTGRES_ADMIN":"Host=127.0.0.1;Database=shop_test_control;Username=shop_test_runner;Password="+password+";Maximum Pool Size=4","TMPDIR":"/stage/target/tmp","DOTNET_PROCESSOR_COUNT":"1","DOTNET_EnableDiagnostics":"0","DOTNET_CLI_TELEMETRY_OPTOUT":"1"}
		env(root/(side+".env"),values)
	m={"run":run,"scope":"synthetic_acc_abad_step1","containers":{},"bundle":{str(f.relative_to(root/"build/bin")):digest(f) for f in (root/"build/bin").rglob("*") if f.is_file()},"legacy_script":digest(root/"tools/cutover_legacy_runtime.py")}
	assert m["bundle"] and (root/"build/bin/Platform.AotSmoke.dll").is_file()
	save(root/"pair.private.json",m)
	for role in CAPS:
		_,_,_,_,_,flags=config(root,run,role)
		m["containers"][role]={"id":call(["create",*flags])};save(root/"pair.private.json",m)
		validate(root,m)
	return m
def tree_fingerprint(path):
	items=[]
	for f in sorted([path,*path.rglob("*")]):
		s=f.lstat();assert not stat.S_ISLNK(s.st_mode)
		if f.is_file(): assert stat.S_ISREG(s.st_mode) and s.st_nlink==1
		else: assert f.is_dir()
		items.append((str(f.relative_to(path)),s.st_uid,s.st_gid,stat.S_IMODE(s.st_mode),digest(f) if f.is_file() else "directory"))
	return hashlib.sha256(json.dumps(items,separators=(",",":")).encode()).hexdigest()
def copy_owned(source,destination):
	shutil.copytree(source,destination,copy_function=shutil.copy2)
	for f in [source,*source.rglob("*")]:
		s=f.lstat();assert not stat.S_ISLNK(s.st_mode);os.chown(destination/f.relative_to(source),s.st_uid,s.st_gid)
def prepare_clone(root,source):
	assert source!=root and re.fullmatch(r"/var/lib/teleg-cli/t068-pair-[a-z0-9-]+",str(source)) and source.resolve(strict=True)==source and source.stat().st_uid==0 and stat.S_IMODE(source.stat().st_mode)==0o700
	old=json.loads(private(source/"pair.private.json"));validate(source,old)
	assert all(not inspect(v["id"])["State"]["Running"] for v in old["containers"].values())
	assert not (root/"pair.private.json").exists()
	run=uuid.uuid4().hex;bindings={}
	for side,uid,pguid in [("legacy",1000,70),("target",10001,999)]:
		data=side+"-db";before=tree_fingerprint(source/data);copy_owned(source/data,root/data);assert tree_fingerprint(root/data)==before
		bindings[data]=before
		directory(root/side,uid)
		for name in ["requests","results","attempts","logs"]: directory(root/side/name,uid)
		before=tree_fingerprint(source/side/"tmp");copy_owned(source/side/"tmp",root/side/"tmp");assert tree_fingerprint(root/side/"tmp")==before
		for name in ["requests","results","attempts"]:
			shutil.copytree(source/side/name,root/side/("step1-"+name),copy_function=shutil.copy2)
		for name in [side+".env",data+".env"]: env(root/name,dict(line.split("=",1) for line in private(source/name).splitlines()))
		state=json.loads((source/side/"state.json").read_text());state["run"]=run if side=="legacy" else str(uuid.UUID(hex=run))
		save(root/side/"state.json",state);os.chown(root/side/"state.json",uid,uid)
		f=root/side/"identity";f.write_text(run+"\n");f.chmod(0o600);os.chown(f,uid,uid)
		if side=="legacy":
			e=root/(side+".env");vals=dict(line.split("=",1) for line in private(e).splitlines());vals["PGAPPNAME"]="t068-cutover-stage";e.unlink();env(e,vals)
		assert tree_fingerprint(source/data)==bindings[data]
	m={"run":run,"scope":"synthetic_acc_abad_fence","cloned_from_run":old["run"],"source_copy_fingerprints":bindings,"containers":{},"bundle":{str(f.relative_to(root/"build/bin")):digest(f) for f in (root/"build/bin").rglob("*") if f.is_file()},"legacy_script":digest(root/"tools/cutover_legacy_runtime.py")}
	assert (root/"build/bin/Platform.AotSmoke.dll").is_file()
	save(root/"pair.private.json",m)
	for role in CAPS:
		_,_,_,_,_,flags=config(root,run,role);m["containers"][role]={"id":call(["create",*flags])};save(root/"pair.private.json",m);validate(root,m)
	validate(source,old);assert all(not inspect(v["id"])["State"]["Running"] for v in old["containers"].values())
	return m
def start(root,m,side=None):
	if (root/"fence-header.private.json").exists():
		from cutover_fence import consume_start
		consume_start(root,m,side)
	sides=[side] if side else ["legacy","target"]
	assert set(m["containers"])==set(CAPS)
	assert all(not inspect(m["containers"][r]["id"])["State"]["Running"] for r in sides)
	validate(root,m)
	for role in [s+"-db" for s in sides]:
		call(["start",m["containers"][role]["id"]])
	for role,user,database in [("legacy-db","t068_legacy_owner","t068_legacy_stage"),("target-db","shop_test_runner","shop_test_control")]:
		if role[:-3] not in sides: continue
		for _ in range(90):
			p=subprocess.run(["docker","exec",m["containers"][role]["id"],"pg_isready","-q","-h","127.0.0.1","-U",user,"-d",database],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
			if p.returncode==0: break
			time.sleep(1)
		else: raise ValueError()
	for role in sides:
		seal_auth(root,role);verify_auth(root,m,role)
		if not (root/role/"state.json").exists():
			_,_,_,_,_,flags=config(root,m["run"],role);flags=flags.copy();flags[1]+="-init";flags[-1]="init"
			log=root/(role+"-init.private.log")
			with log.open("wb") as output: result=subprocess.run(["docker","run","--rm",*flags],stdout=output,stderr=subprocess.STDOUT,timeout=180)
			(root/(role+"-init.exit")).write_text(str(result.returncode)+"\n")
			assert result.returncode==0
		ready=root/role/"ready.json"
		if ready.exists(): ready.rename(root/role/("ready-"+uuid.uuid4().hex+".json"))
		call(["start",m["containers"][role]["id"]])
		for _ in range(120):
			if ready.is_file(): break
			assert inspect(m["containers"][role]["id"])["State"]["Running"]
			time.sleep(.5)
		else: raise ValueError()
	validate(root,m)
def stop(root,m,side=None):
	assert set(m["containers"]).issubset(CAPS)
	for role in ["legacy","target","legacy-db","target-db"]:
		if role not in m["containers"] or (side and role not in {side,side+"-db"}): continue
		name,image,_,_,_,_=config(root,m["run"],role);x=inspect(m["containers"][role]["id"])
		assert x["Id"]==m["containers"][role]["id"] and x["Name"]=="/"+name and x["Image"]==image and x["Config"]["Labels"]["teleg.run"]==m["run"] and x["Config"]["Labels"]["teleg.task"]=="T068"
		call(["stop","--time","15",x["Id"]],45)
def request(root,side,command,request_id=None,_fenced=False,_wait=True):
	if (root/"fence-header.private.json").exists() and not _fenced: raise ValueError("Use epoch-fenced admission")
	key=request_id or uuid.uuid4().hex
	assert side in {"legacy","target"} and re.fullmatch("[a-f0-9]{32}",key)
	path=root/side/"requests"/(key+".request");uid=1000 if side=="legacy" else 10001
	lock=os.open(root/"request.lock",os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW,0o600)
	try:
		fcntl.flock(lock,fcntl.LOCK_EX)
		if path.exists():
			s=path.lstat();assert stat.S_ISREG(s.st_mode) and s.st_nlink==1 and s.st_uid==uid and stat.S_IMODE(s.st_mode)==0o600 and s.st_size<=128 and path.read_text()==command+"\n"
		else:
			temp=root/side/(".request-"+uuid.uuid4().hex+".tmp")
			fd=os.open(temp,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
			try: os.write(fd,(command+"\n").encode());os.fchown(fd,uid,uid);os.fsync(fd)
			finally: os.close(fd)
			os.rename(temp,path)
	finally: os.close(lock)
	if not _wait: return key,None
	result=root/side/"results"/(key+".json")
	for _ in range(120):
		if result.is_file(): return key,json.loads(result.read_text())
		time.sleep(.25)
	raise ValueError()
def main():
	a=argparse.ArgumentParser();a.add_argument("action",choices=["prepare","start","stop","validate","status"]);a.add_argument("--root",required=True);a.add_argument("--from-root");a.add_argument("--side",choices=["legacy","target"]);args=a.parse_args()
	root=Path(args.root)
	if os.geteuid()!=0 or not re.fullmatch(r"/var/lib/teleg-cli/t068-pair-[a-z0-9-]+",str(root)) or root.resolve(strict=True)!=root or root.stat().st_uid!=0 or stat.S_IMODE(root.stat().st_mode)!=0o700: raise ValueError()
	m=(prepare_clone(root,Path(args.from_root)) if args.from_root else prepare(root)) if args.action=="prepare" else json.loads(private(root/"pair.private.json"))
	if args.action=="start": start(root,m,args.side)
	elif args.action=="stop": stop(root,m,args.side)
	else: validate(root,m)
	if args.action=="status": print(json.dumps({role:inspect(v["id"])["State"]["Running"] for role,v in m["containers"].items()},sort_keys=True))
if __name__=="__main__":
	try: main()
	except Exception:
		sys.stderr.write("Synthetic pair operation rejected; inspect private logs.\n");raise SystemExit(78)
