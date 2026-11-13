import argparse,json,os,re,subprocess,time
from pathlib import Path
import cutover_pair as p
if not __debug__: raise SystemExit(78)
def run(root):
	assert os.geteuid()==0 and re.fullmatch(r'/var/lib/teleg-cli/t068-pair-[a-z0-9-]+',str(root)) and root.resolve(strict=True)==root and root.stat().st_uid==0 and root.stat().st_mode&0o777==0o700
	m=json.loads(p.private(root/'pair.private.json'));p.validate(root,m);assert 'cloned_from_run' in m and not (root/'fence-header.private.json').exists() and all(not p.inspect(v['id'])['State']['Running'] for v in m['containers'].values())
	assert not (root/'legacy/callback-key.json').exists() and not (root/'legacy-callback-result.private.json').exists()
	before={v['Id']:v for v in json.loads(p.call(['inspect',*p.call(['ps','-aq']).splitlines()])) if v['Id'] not in {x['id'] for x in m['containers'].values()}};p.save(root/'legacy-callback-preexisting.private.json',before)
	source=Path('/var/lib/teleg-cli/t068-pair-20261112');original=json.loads(p.private(source/'pair.private.json'));p.validate(source,original);assert m['cloned_from_run']==original['run'] and all(not p.inspect(v['id'])['State']['Running'] for v in original['containers'].values());source_hashes={side:p.tree_fingerprint(source/(side+'-db')) for side in ['legacy','target']};target=p.tree_fingerprint(root/'target-db');phases=[]
	def actor(mode,expected=0,tag=None):
		tag=tag or mode
		p.validate(root,m);assert not p.inspect(m['containers']['target-db']['id'])['State']['Running']
		name,image,uid,memory,mounts,flags=p.config(root,m['run'],'legacy');flags=flags.copy();flags[1]+='-callback-'+tag;index=flags.index(image);flags[index:index]=['--mount','type=bind,source='+str(root/'tools/cutover_legacy_callback.py')+',target=/runtime/callback.py,readonly'];flags[-2:]=['/runtime/callback.py',mode]
		id=p.call(['create',*flags]);x=p.inspect(id);h=x['HostConfig'];assert x['Image']==p.LEGACY and x['Config']['User']=='1000:1000' and x['Config']['Cmd']==['/runtime/callback.py',mode] and x['Config']['Entrypoint']==['python3'] and h['ReadonlyRootfs'] and h['CapDrop']==['ALL'] and not h['Privileged'] and not h['PortBindings'] and h['Memory']==h['MemorySwap']==memory and h['PidsLimit']==96 and h['RestartPolicy']['Name']=='no' and 'no-new-privileges' in h['SecurityOpt'] and h['Ulimits']==[{'Name':'fsize','Hard':67108864,'Soft':67108864}] and h['NetworkMode'] in {'container:'+m['containers']['legacy-db']['id'],'container:'+p.config(root,m['run'],'legacy-db')[0]}
		assert sorted((q['Source'],q['Destination'],q['RW']) for q in x['Mounts'])==sorted(mounts+[(str(root/'tools/cutover_legacy_callback.py'),'/runtime/callback.py',False)])
		p.save(root/('legacy-callback-'+tag+'-configuration.private.json'),x)
		try:
			with (root/('legacy-callback-'+tag+'.private.log')).open('wb') as log:
				if mode=='accept-crash':
					process=subprocess.Popen(['docker','start','-a',id],stdout=log,stderr=subprocess.STDOUT);end=time.monotonic()+90
					while not (root/'legacy/callback-commit-marker.json').exists() and time.monotonic()<end:
						assert process.poll() is None;time.sleep(.1)
					assert json.loads((root/'legacy/callback-commit-marker.json').read_text())=={'run':m['run'],'committed':True};p.call(['kill','--signal','KILL',id]);result=subprocess.CompletedProcess(process.args,process.wait(timeout=30))
				else: result=subprocess.run(['docker','start','-a',id],stdout=log,stderr=subprocess.STDOUT,timeout=120)
		finally:
			if p.inspect(id)['State']['Running']: p.call(['stop','--time','5',id])
		x=p.inspect(id);p.save(root/('legacy-callback-'+tag+'-execution.private.json'),{'container':id,'exit':x['State']['ExitCode'],'oom':x['State']['OOMKilled'],'process_exit':result.returncode,'started':x['State']['StartedAt'],'finished':x['State']['FinishedAt'],'helper_sha256':p.digest(root/'tools/cutover_legacy_callback.py')});assert not x['State']['Running'] and not x['State']['OOMKilled'] and x['State']['ExitCode']==result.returncode==expected
		if expected!=0: pass
		elif mode=='init': assert (root/'legacy/callback-before.json').is_file()
		elif mode!='accept-crash': assert json.loads((root/'legacy'/('callback-'+mode+'-result.json')).read_text())['mode']==mode
		phases.append(tag);p.save(root/'legacy-callback-progress.private.json',{'phases':phases})
	try:
		p.start(root,m,'legacy')
		actor('invalid',78);actor('review',78,'review-empty');actor('init');actor('init',78,'init-repeated')
		for mode in ['reject','pending']: actor(mode)
		actor('accept-crash',137);actor('review');p.stop(root,m,'legacy');p.start(root,m,'legacy');actor('replay');p.stop(root,m)
		assert p.tree_fingerprint(root/'target-db')==target
		for side,value in source_hashes.items(): assert p.tree_fingerprint(source/(side+'-db'))==value
		def stable(x): return {'config':x['Config'],'host':x['HostConfig'],'mounts':sorted(x['Mounts'],key=lambda v:v['Destination']),'state':{k:x['State'][k] for k in ['Running','StartedAt','FinishedAt','Restarting']},'restarts':x['RestartCount'],'image':x['Image']}
		for id,value in before.items(): assert stable(p.inspect(id))==stable(value)
		p.validate(root,m);assert all(not p.inspect(v['id'])['State']['Running'] for v in m['containers'].values())
		p.save(root/'legacy-callback-result.private.json',{'run':m['run'],'phases':phases,'preexisting_containers_unchanged':len(before),'source_clones_unchanged':True,'target_database_never_started_and_unchanged':True,'all_four_pair_containers_stopped':True,'actual_legacy_webhook_used':True,'synthetic_credit_count':1,'notification_jobs_added':1,'notification_worker_started':False,'provider_or_telegram_used':False,'durable_inbox_legacy_dispatch_verified':False,'shared_core_lifecycle_verified':False,'protected_copy_verified':False,'task_complete':False,'release_ready':False})
	finally: p.stop(root,m)
if __name__=='__main__':
	a=argparse.ArgumentParser();a.add_argument('--root',required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
