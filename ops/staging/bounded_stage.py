import hashlib
import json
import os
from pathlib import Path
import stat
import shutil
import subprocess
import sys
import time

ROOT=Path('/var/lib/teleg-cli/shop-staging')
WORK=Path('/var/lib/teleg-cli/t076-disk-bound-20261116')
IMAGE=WORK/'stage-copy.img'
STATE=WORK/'bounded-state.json'
OVERRIDE=WORK/'bounded-compose.json'
NATIVE=Path('/var/lib/teleg-cli/t076-aot-20261115/Platform.Api.published/Platform.Api')
VOLUME='shop-t076-bounded-stage'
BASE=ROOT/'compose.json'
ROLES=('api','bot-a','bot-b','bot-c','jobs')

def call(*args,**kw):
	return subprocess.run(args,check=True,text=True,**kw)

def output(*args):
	return call(*args,stdout=subprocess.PIPE).stdout.strip()

def require(condition,message):
	if not condition: raise RuntimeError(message)

def compose(*args):
	return call('docker','compose','-f',str(BASE),'-f',str(OVERRIDE),*args)

def check_file(path):
	s=path.lstat()
	require(stat.S_ISREG(s.st_mode) and s.st_uid==0 and s.st_nlink==1,'Unsafe protected file')

def up():
	require(not STATE.exists() and not OVERRIDE.exists(),'Previous bounded state requires cleanup')
	for p in (BASE,IMAGE,NATIVE): check_file(p)
	base=json.loads(BASE.read_text())
	require(base.get('name')=='shop-staging' and set(base['services'])=={'stage-db',*('stage-'+x for x in ROLES)},'Unexpected staging project')
	for name,service in base['services'].items():
		require(service.get('read_only') is True and service.get('restart')=='no' and service.get('cpus')==0.18 and service.get('pids_limit')==55 and not service.get('ports'),'Unsafe staging resource settings')
		require(service.get('mem_limit')==('160m' if name=='stage-db' else '112m') and service.get('memswap_limit')==service.get('mem_limit'),'Unsafe staging memory limit')
		require(service.get('logging',{}).get('options')=={'max-size':'1m','max-file':'2'},'Unsafe staging log limit')
		require(service.get('cap_drop')==['ALL'] and service.get('security_opt')==['no-new-privileges:true'],'Unsafe container privileges')
		if name!='stage-db':
			role=name.removeprefix('stage-');expected=[str(ROOT/'runtime')+':/opt/runtime:ro',str(ROOT/'roles'/role)+':/run/staging:ro']
			if role.startswith('bot-'): expected.append(str(ROOT/'assets'/role[-1])+':/run/receipt:ro')
			require(service.get('volumes')==expected,'Unexpected service mounts')
		else: require(service.get('volumes')==[str(ROOT/'bootstrap/root-password')+':/run/bootstrap/root-password:ro','postgres:/var/lib/postgresql'],'Unexpected database mounts')
	network=base['networks']['default']
	require(network.get('internal') is True and network.get('driver_opts',{}).get('com.docker.network.bridge.gateway_mode_ipv4')=='isolated','Staging network is not isolated')
	require(IMAGE.stat().st_size==512*1024*1024,'Unexpected bounded image size')
	require(hashlib.sha256(NATIVE.read_bytes()).hexdigest()=='1dd09070d6ac6339987014d72d1f6572874b0380377039690fde582566df97c5','Native artifact identity mismatch')
	require(shutil.disk_usage(WORK).free>=3*1024**3,'Insufficient host disk headroom')
	available=int(next(x for x in Path('/proc/meminfo').read_text().splitlines() if x.startswith('MemAvailable:')).split()[1])
	require(available>=1048576,'Insufficient host memory headroom')
	require(len(os.sched_getaffinity(0))>=2,'Insufficient host CPU capacity')
	require(not output('docker','ps','-q','--filter','label=com.docker.compose.project=shop-staging'),'Staging project is already running')
	require(subprocess.run(['docker','volume','inspect',VOLUME],capture_output=True).returncode!=0,'Bounded volume already exists')
	require(not output('/sbin/losetup','-j',str(IMAGE)),'Image already attached')
	uuid=output('/sbin/blkid','-s','UUID','-o','value',str(IMAGE))
	require(uuid==sys.argv[2],'Bounded filesystem identity mismatch')
	loop=output('/sbin/losetup','--find','--show',str(IMAGE))
	STATE.write_text(json.dumps({'loop':loop,'uuid':uuid,'image':str(IMAGE)}));STATE.chmod(0o600)
	call('docker','volume','create','--driver','local','--opt','type=ext4','--opt','device='+loop,'--opt','o=nosuid,nodev,noexec',VOLUME,stdout=subprocess.DEVNULL)
	services={'stage-db':{'cpus':0.1,'mem_limit':'128m','memswap_limit':'128m','volumes':[{'type':'volume','source':'bounded','target':'/var/lib/postgresql'}]}}
	for role in ROLES:
		services['stage-'+role]={'cpus':0.1,'mem_limit':'64m','memswap_limit':'64m','entrypoint':['/opt/t076/Platform.Api.native','--serve-synthetic-staging',role],'volumes':[str(NATIVE)+':/opt/t076/Platform.Api.native:ro']}
	OVERRIDE.write_text(json.dumps({'services':services,'volumes':{'bounded':{'external':True,'name':VOLUME}}}));OVERRIDE.chmod(0o600)
	config=json.loads(output('docker','compose','-f',str(BASE),'-f',str(OVERRIDE),'config','--format','json'))
	mounts=[x for x in config['services']['stage-db']['volumes'] if x['target']=='/var/lib/postgresql']
	require(len(mounts)==1 and mounts[0]['source']=='bounded','Database mount override failed')
	require(all(float(v.get('cpus',0))==0.1 for v in config['services'].values()),'Staging CPU reserve guard failed')
	require(all(int(v.get('mem_limit',0))==(134217728 if k=='stage-db' else 67108864) and v.get('memswap_limit')==v.get('mem_limit') for k,v in config['services'].items()),'Staging memory reserve guard failed')
	compose('up','-d','--no-deps','--pull','never','stage-db')
	for _ in range(40):
		p=subprocess.run(['docker','exec','shop-staging-stage-db-1','pg_isready','-q','-h','127.0.0.1','-U','postgres','-d','shop_staging'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
		if p.returncode==0: break
		time.sleep(1)
	else: raise RuntimeError('Bounded database failed readiness')
	compose('up','-d','--no-deps','--pull','never',*('stage-'+x for x in ROLES))
	for role in ROLES:
		for _ in range(60):
			p=subprocess.run(['docker','exec','shop-staging-stage-'+role+'-1','curl','-ksS','--max-time','2','https://127.0.0.1:8443/ready'],capture_output=True,text=True)
			if p.returncode==0 and p.stdout==role: break
			time.sleep(1)
		else: raise RuntimeError('Synthetic service failed readiness: '+role)
	print('Bounded synthetic staging started')

def down():
	check_file(STATE)
	s=json.loads(STATE.read_text());loop=s['loop']
	require(s['image']==str(IMAGE) and loop.startswith('/dev/loop'),'Unexpected state identity')
	require(output('/sbin/losetup','-n','-O','BACK-FILE',loop).splitlines()[0]==str(IMAGE),'Loop backing identity mismatch')
	has_override=OVERRIDE.exists()
	if has_override:
		check_file(OVERRIDE);compose('stop','-t','10')
	else:
		require(not output('docker','ps','-q','--filter','label=com.docker.compose.project=shop-staging'),'Cannot clean partial state while staging runs')
	cid=output('docker','ps','-aq','--filter','name=^/shop-staging-stage-db-1$')
	if cid and has_override:
		require(output('docker','inspect','-f','{{index .Config.Labels "com.docker.compose.project"}}',cid)=='shop-staging','Database container identity mismatch')
		call('docker','rm',cid,stdout=subprocess.DEVNULL)
	v=subprocess.run(['docker','volume','inspect',VOLUME],capture_output=True,text=True)
	if v.returncode==0:
		options=json.loads(v.stdout)[0].get('Options') or {}
		require(options.get('device')==loop and options.get('type')=='ext4','Bounded volume identity mismatch')
		call('docker','volume','rm',VOLUME,stdout=subprocess.DEVNULL)
	call('/sbin/losetup','-d',loop)
	STATE.unlink();OVERRIDE.unlink(missing_ok=True)
	print('Bounded synthetic staging stopped')

if __name__=='__main__':
	if os.geteuid()!=0 or len(sys.argv) not in (2,3): raise SystemExit('Usage: bounded_stage.py up FILESYSTEM_UUID | down')
	try:
		if sys.argv[1]=='up' and len(sys.argv)==3: up()
		elif sys.argv[1]=='down' and len(sys.argv)==2: down()
		else: raise ValueError('Invalid operation')
	except (OSError,RuntimeError,ValueError,subprocess.CalledProcessError) as e:
		print('Bounded staging rejected:',str(e),file=sys.stderr);raise SystemExit(1)
