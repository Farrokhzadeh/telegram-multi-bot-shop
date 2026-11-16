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
NATIVE=Path('/var/lib/teleg-cli/t076-aot-20261115/Platform.Api.native')
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
	require(IMAGE.stat().st_size==512*1024*1024,'Unexpected bounded image size')
	require(hashlib.sha256(NATIVE.read_bytes()).hexdigest()=='1f23ba0c6d1fb6bd00be4b1ecad88a5bcff303c65ed4348d6d5a3b9b9c816622','Native artifact identity mismatch')
	require(shutil.disk_usage(WORK).free>=3*1024**3,'Insufficient host disk headroom')
	available=int(next(x for x in Path('/proc/meminfo').read_text().splitlines() if x.startswith('MemAvailable:')).split()[1])
	require(available>=1048576,'Insufficient host memory headroom')
	require(not output('docker','ps','-q','--filter','label=com.docker.compose.project=shop-staging'),'Staging project is already running')
	require(subprocess.run(['docker','volume','inspect',VOLUME],capture_output=True).returncode!=0,'Bounded volume already exists')
	require(not output('/sbin/losetup','-j',str(IMAGE)),'Image already attached')
	uuid=output('/sbin/blkid','-s','UUID','-o','value',str(IMAGE))
	require(uuid==sys.argv[2],'Bounded filesystem identity mismatch')
	loop=output('/sbin/losetup','--find','--show',str(IMAGE))
	STATE.write_text(json.dumps({'loop':loop,'uuid':uuid,'image':str(IMAGE)}));STATE.chmod(0o600)
	call('docker','volume','create','--driver','local','--opt','type=ext4','--opt','device='+loop,'--opt','o=nosuid,nodev,noexec',VOLUME,stdout=subprocess.DEVNULL)
	services={'stage-db':{'volumes':[{'type':'volume','source':'bounded','target':'/var/lib/postgresql'}]}}
	for role in ROLES:
		services['stage-'+role]={'entrypoint':['/opt/t076/Platform.Api.native','--serve-synthetic-staging',role],'volumes':[str(NATIVE)+':/opt/t076/Platform.Api.native:ro']}
	OVERRIDE.write_text(json.dumps({'services':services,'volumes':{'bounded':{'external':True,'name':VOLUME}}}));OVERRIDE.chmod(0o600)
	config=json.loads(output('docker','compose','-f',str(BASE),'-f',str(OVERRIDE),'config','--format','json'))
	mounts=[x for x in config['services']['stage-db']['volumes'] if x['target']=='/var/lib/postgresql']
	require(len(mounts)==1 and mounts[0]['source']=='bounded','Database mount override failed')
	compose('up','-d','--no-deps','--pull','never','stage-db')
	for _ in range(40):
		p=subprocess.run(['docker','exec','shop-staging-stage-db-1','pg_isready','-q','-h','127.0.0.1','-U','postgres','-d','shop_staging'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
		if p.returncode==0: break
		time.sleep(1)
	else: raise RuntimeError('Bounded database failed readiness')
	compose('up','-d','--no-deps','--pull','never',*('stage-'+x for x in ROLES))
	print('Bounded synthetic staging started')

def down():
	check_file(STATE);check_file(OVERRIDE)
	s=json.loads(STATE.read_text());loop=s['loop']
	require(s['image']==str(IMAGE) and loop.startswith('/dev/loop'),'Unexpected state identity')
	require(output('/sbin/losetup','-n','-O','BACK-FILE',loop).splitlines()[0]==str(IMAGE),'Loop backing identity mismatch')
	compose('stop','-t','10')
	cid=output('docker','ps','-aq','--filter','name=^/shop-staging-stage-db-1$')
	if cid:
		require(output('docker','inspect','-f','{{index .Config.Labels "com.docker.compose.project"}}',cid)=='shop-staging','Database container identity mismatch')
		call('docker','rm',cid,stdout=subprocess.DEVNULL)
	call('docker','volume','rm',VOLUME,stdout=subprocess.DEVNULL)
	call('/sbin/losetup','-d',loop)
	STATE.unlink();OVERRIDE.unlink()
	print('Bounded synthetic staging stopped')

if __name__=='__main__':
	if os.geteuid()!=0 or len(sys.argv) not in (2,3): raise SystemExit('Usage: bounded_stage.py up FILESYSTEM_UUID | down')
	try:
		if sys.argv[1]=='up' and len(sys.argv)==3: up()
		elif sys.argv[1]=='down' and len(sys.argv)==2: down()
		else: raise ValueError('Invalid operation')
	except (OSError,RuntimeError,ValueError,subprocess.CalledProcessError) as e:
		print('Bounded staging rejected:',str(e),file=sys.stderr);raise SystemExit(1)
