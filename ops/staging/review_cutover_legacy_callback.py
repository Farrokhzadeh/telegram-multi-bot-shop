import argparse,base64,hashlib,json,os,re,subprocess,uuid
from pathlib import Path
import cutover_pair as p
from cutover_fence import owned
if not __debug__: raise SystemExit(78)
def run(root):
	assert os.geteuid()==0 and re.fullmatch(r'/var/lib/teleg-cli/t068-pair-[a-z0-9-]+',str(root)) and root.resolve(strict=True)==root and root.stat().st_uid==0 and root.stat().st_mode&0o777==0o700
	m=json.loads(p.private(root/'pair.private.json'));p.validate(root,m);assert all(not p.inspect(v['id'])['State']['Running'] for v in m['containers'].values()) and (root/'execution.exit').read_text().strip()=='0'
	assert all(p.inspect(m['containers'][side]['id'])['State']['StartedAt'].startswith('0001-') for side in ['target','target-db'])
	result=json.loads(p.private(root/'legacy-callback-result.private.json'));assert result['phases']==['invalid','review-empty','init','init-repeated','reject','pending','accept-crash','review','replay'] and result['task_complete'] is result['release_ready'] is False
	probe=json.loads(p.private(root/'metadata-probe-created.private.json'));observed=p.inspect(probe['Id']);parent=p.inspect(observed['HostConfig']['NetworkMode'].split(':',1)[1]);assert probe['Config']['Hostname']==probe['Id'][:12] and observed['Config']['Hostname']==parent['Config']['Hostname'] and probe['HostConfig']['OomKillDisable'] is False and observed['HostConfig']['OomKillDisable'] is None and observed['State']['ExitCode']==0 and not observed['State']['Running'] and not observed['State']['OOMKilled'] and observed['Image']==parent['Image']==p.LEGACY and not observed['Mounts'] and not parent['Mounts']
	assert {k for k in probe['Config'] if probe['Config'][k]!=observed['Config'][k]}=={'Hostname'} and {k for k in probe['HostConfig'] if probe['HostConfig'][k]!=observed['HostConfig'][k]}=={'OomKillDisable'}
	for tag in result['phases']:
		execution=json.loads(p.private(root/('legacy-callback-'+tag+'-execution.private.json')));x=p.inspect(execution['container']);config=json.loads(p.private(root/('legacy-callback-'+tag+'-configuration.private.json')))
		expected_config=dict(config['Config']);expected_host=dict(config['HostConfig']);assert expected_config['Hostname']==x['Id'][:12] and expected_host['OomKillDisable'] is False;expected_config['Hostname']=p.inspect(m['containers']['legacy-db']['id'])['Config']['Hostname'];expected_host['OomKillDisable']=None
		assert x['Config']==expected_config and x['HostConfig']==expected_host and x['State']['ExitCode']==execution['process_exit']==execution['exit']==(137 if tag=='accept-crash' else 78 if tag in {'invalid','review-empty','init-repeated'} else 0) and not x['State']['Running'] and not x['State']['OOMKilled'] and execution['helper_sha256']==p.digest(root/'tools/cutover_legacy_callback.py')
	before=owned(root,'legacy','callback-before.json')['tables'];accepted=owned(root,'legacy','callback-accepted.json');assert set(before)==set(accepted) and {t for t in before if before[t]!=accepted[t]}=={'users','transactions','payment_attempts','background_jobs','user_activity'}
	assert len(accepted)==56 and all(re.fullmatch('[a-z_][a-z_0-9]*',t) for t in accepted)
	query="BEGIN READ ONLY;\nSET LOCAL TIME ZONE 'UTC';\n"
	for table in sorted(accepted):
		query+="SELECT 'T068_LEGACY_COUNT|"+table+"|'||count(*)||'|'||current_setting('transaction_read_only') FROM public.\""+table+"\";\nSELECT 'T068_LEGACY_ROW|"+table+"|'||replace(encode(convert_to(row_to_json(t)::text,'UTF8'),'base64'),E'\\n','')||'|'||current_setting('transaction_read_only') FROM public.\""+table+"\" t ORDER BY row_to_json(t)::text;\n"
	query+='ROLLBACK;\n';name='teleg-t068-callback-audit-'+uuid.uuid4().hex[:12]
	id=p.call(['create','-i','--name',name,'--label','teleg.task=T068','--label','teleg.purpose=synthetic-legacy-callback-review','--user','70:70','--network','none','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges','--cpus','.25','--memory','96m','--memory-swap','96m','--pids-limit','64','--ulimit','fsize=67108864:67108864','--tmpfs','/tmp:rw,nosuid,size=16m','--mount','type=bind,source='+str(root/'legacy-db')+',target=/var/lib/postgresql/data','--entrypoint','postgres',p.PG['legacy'],'--single','-D','/var/lib/postgresql/data','-c','shared_buffers=16MB','-c','default_transaction_read_only=on','t068_legacy_stage'])
	x=p.inspect(id);assert x['Image']==p.PG['legacy'] and x['Config']['User']=='70:70' and x['HostConfig']['NetworkMode']=='none' and not x['HostConfig']['PortBindings'] and x['HostConfig']['ReadonlyRootfs'] and [(v['Source'],v['Destination'],v['RW']) for v in x['Mounts']]==[(str(root/'legacy-db'),'/var/lib/postgresql/data',True)];p.save(root/'legacy-callback-audit-configuration.private.json',x)
	try:
		check=subprocess.run(['docker','start','-ai',id],input=query.encode(),capture_output=True,timeout=120)
	finally:
		if p.inspect(id)['State']['Running']: p.call(['stop','--time','5',id])
	(root/'legacy-callback-audit.private.log').write_bytes(check.stdout+check.stderr);x=p.inspect(id);assert check.returncode==x['State']['ExitCode']==0 and not x['State']['OOMKilled'];counts=re.findall(rb'T068_LEGACY_COUNT\|([a-z_0-9]+)\|(\d+)\|on',check.stdout);assert len(counts)==len(dict(counts))==56
	values={t.decode():[] for t,n in counts}
	for table,raw in re.findall(rb'T068_LEGACY_ROW\|([a-z_0-9]+)\|([A-Za-z0-9+/=]+)\|on',check.stdout): values[table.decode()].append(base64.b64decode(raw,validate=True).decode())
	observed={t:{'count':len(v),'sha256':hashlib.sha256(json.dumps(v,separators=(',',':')).encode()).hexdigest()} for t,v in values.items()};assert all(len(values[t.decode()])==int(n) for t,n in counts) and observed==accepted

	p.save(root/'legacy-callback-audit-execution.private.json',{'container':id,'exit':check.returncode,'sql_sha256':__import__('hashlib').sha256(query.encode()).hexdigest(),'log_sha256':p.digest(root/'legacy-callback-audit.private.log')})
	original=Path('/var/lib/teleg-cli/t068-pair-20261112');source=json.loads(p.private(original/'pair.private.json'));p.validate(original,source);assert all(not p.inspect(v['id'])['State']['Running'] for v in source['containers'].values())
	for side,value in m['source_copy_fingerprints'].items(): assert p.tree_fingerprint(original/side)==value
	def stable(x): return {'config':x['Config'],'host':x['HostConfig'],'mounts':sorted(x['Mounts'],key=lambda v:v['Destination']),'state':{k:x['State'][k] for k in ['Running','StartedAt','FinishedAt','Restarting']},'restarts':x['RestartCount'],'image':x['Image']}
	preexisting=json.loads(p.private(root/'legacy-callback-preexisting.private.json'))
	for id,x in preexisting.items(): assert stable(p.inspect(id))==stable(x)
	p.validate(root,m);assert all(not p.inspect(v['id'])['State']['Running'] for v in m['containers'].values());assert (root/'guards.exit').read_text().strip()=='0'
	evidence={'base_main':'aec45e3f20e5622ab3290f41707b6e531e133b47','scope':'generated-data actual pinned legacy payment webhook component; no durable inbox dispatch or paired handoff claim','legacy_revision':'66371a23a1c8149dc5bdf89a0355aa689d454dba','legacy_image':p.LEGACY,'host_guards':39,'runtime_phases':result['phases'],'independent_stopped_tables_verified':56,'unchanged_legacy_tables':51,'synthetic_credits_added':1,'pending_notification_jobs_added':1,'post_commit_sigkill_exit':137,'preexisting_containers_unchanged':len(preexisting),'all_four_pair_containers_stopped':True,'original_copies_unchanged':True,'real_provider_used':False,'telegram_sender_used':False,'durable_legacy_inbox_dispatch_verified':False,'shared_core_lifecycle_verified':False,'protected_copy_rehearsal_verified':False,'step3_complete':False,'task_complete':False,'release_ready':False,'source_sha256':{n:p.digest(root/'tools'/n) for n in ['cutover_legacy_callback.py','test_cutover_legacy_callback_runtime.py','review_cutover_legacy_callback.py','cutover_pair.py','cutover_legacy_runtime.py']},'evidence_sha256':{str(path.relative_to(root)):p.digest(path) for path in sorted([*root.glob('legacy-callback-*-execution.private.json'),root/'legacy-callback-result.private.json',root/'legacy-callback-audit.private.log',root/'guards.private.log',root/'metadata-probe-result.private.json',root/'metadata-probe-created.private.json',root/'metadata-probe-started.private.json'])}}
	p.save(root/'legacy-callback.redacted.json',evidence);print(json.dumps(evidence,sort_keys=True))
if __name__=='__main__':
	a=argparse.ArgumentParser();a.add_argument('--root',required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
