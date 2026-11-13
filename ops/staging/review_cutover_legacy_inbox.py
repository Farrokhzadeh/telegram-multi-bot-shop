import argparse,base64,hashlib,hmac,json,os,re,subprocess,uuid
from pathlib import Path
import cutover_legacy_inbox as l
from test_cutover_recovery_runtime import snapshot
p=l.p
f=l.f
i=l.i
if not __debug__: raise SystemExit(78)
def run(root):
	i.directory(root);assert os.geteuid()==0 and re.fullmatch(r'/var/lib/teleg-cli/t068-pair-[a-z0-9-]+',str(root));m=f.model(root);p.validate(root,m);assert f.stopped(m) and (root/'execution.exit').read_text().strip()=='0' and (root/'guards-final.exit').read_text().strip()=='0'
	s=f.chain(root,m)[-1];assert s['phase']=='frozen' and s['target_seen'];l.key(root);result=json.loads(p.private(root/'legacy-inbox-result.private.json'));assert result['coordinator_sigkill_exits']==[-9,-9] and len(result['phases'])==5
	before=f.verify(root,json.loads(p.private(root/'legacy-inbox-before.private.json')));after=f.verify(root,json.loads(p.private(root/'legacy-inbox-after.private.json')));assert snapshot(root)==after and before['rows']['target']==after['rows']['target'] and len(after['rows']['target'])==28 and before['stock']==after['stock'] and before['rows']['legacy']==after['rows']['legacy']
	for tag,event in result['events'].items():
		records=i.read(root,event);first=records[0];raw=f.canonical(first['envelope']);mac=hmac.new(bytes.fromhex(p.private(root/'inbox-ingress.private').strip()),raw,hashlib.sha256).hexdigest();assert first['signature']==mac and first['body_sha256']==hashlib.sha256(raw).hexdigest() and first['verification']==l.verify(root,first['envelope'])
		if tag in {'accepted','unbound'}:
			assert len(records)==3 and records[-1]['phase']==('result_observed' if tag=='accepted' else 'held') and records[-1]['outcome']==('legacy-payment:200' if tag=='accepted' else 'legacy-payment:400')
			execution=f.verify(root,json.loads(p.private(root/('legacy-inbox-'+event+'-execution.private.json'))));x=p.inspect(execution['container']);expected=json.loads(p.private(root/('legacy-inbox-'+event+'-configuration.private.json')));expected['Config']['Hostname']=p.inspect(m['containers']['legacy-db']['id'])['Config']['Hostname'];expected['HostConfig']['OomKillDisable']=None
			assert x['Config']==expected['Config'] and x['HostConfig']==expected['HostConfig'] and not x['State']['Running'] and not x['State']['OOMKilled'] and x['State']['ExitCode']==0 and x['Image']==p.LEGACY and records[-1]['result_sha256']==p.digest(root/'legacy/callback-results'/(event+'.json'))
			v=f.owned(root,'legacy','callback-results/'+event+'.json');assert v['input_sha256']==execution['input_sha256']==p.digest(root/'legacy/callback-inputs'/(event+'.json')) and v['run']==m['run'] and v['event']==event and v['status']==(200 if tag=='accepted' else 400) and f.owned(root,'legacy','callback-inputs/'+event+'.json')==l.payload(root,event)
		elif tag=='uncertain': assert len(records)==2 and l.reconcile(root,event)['phase']=='uncertain_do_not_retry' and not (root/'legacy/callback-results'/(event+'.json')).exists() and not (root/('legacy-inbox-'+event+'-execution.private.json')).exists()
		else: assert tag=='pending' and len(records)==1
	accepted=f.owned(root,'legacy','callback-accepted.json');original=f.owned(root,'legacy','callback-before.json')['tables'];assert len(accepted)==56 and {t for t in accepted if accepted[t]!=original[t]}=={'users','transactions','payment_attempts','background_jobs','user_activity'}
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
	original=Path('/var/lib/teleg-cli/t068-pair-20261112');source=json.loads(p.private(original/'pair.private.json'));p.validate(original,source);assert f.stopped(source)
	for side,value in m['source_copy_fingerprints'].items(): assert p.tree_fingerprint(original/side)==value
	def stable(x): return {'config':x['Config'],'host':x['HostConfig'],'mounts':sorted(x['Mounts'],key=lambda v:v['Destination']),'state':{k:x['State'][k] for k in ['Running','StartedAt','FinishedAt','Restarting']},'restarts':x['RestartCount'],'image':x['Image']}
	preexisting=json.loads(p.private(root/'preexisting.private.json'))
	for id,x in preexisting.items(): assert stable(p.inspect(id))==stable(x)
	running=set();starts=[]
	for line in (root/'legacy-inbox-events.private.jsonl').read_text().splitlines():
		v=json.loads(line);id=v['Actor']['ID'];assert id in {m['containers'][side+'-db']['id'] for side in f.SIDES}
		if v['Action']=='start': running.add(id);starts.append(id);assert len(running)==1
		else: assert v['Action']=='die';running.discard(id)
	assert starts==[m['containers'][side+'-db']['id'] for side in ['legacy','legacy','target']] and not running and f.stopped(m)
	guards=(root/'guards-final.private.log').read_text();assert 'Ran 44 tests' in guards and guards.rstrip().endswith('OK')
	evidence={'base_main':'7c72fd0cd0e0edce9d682fba421b50c3bb2759e7','scope':'generated-data durable legacy payment capture, exclusive dispatch and paired handoff; no public HTTP endpoint','host_guards':44,'runtime_phases':result['phases'],'coordinator_sigkill_exits':[-9,-9],'independent_stopped_legacy_tables':56,'unchanged_legacy_tables':51,'unchanged_target_tables':28,'synthetic_credits_added':1,'pending_notification_jobs_added':1,'database_starts':3,'overlap':False,'preexisting_containers_unchanged':len(preexisting),'all_four_pair_containers_stopped':True,'original_copies_unchanged':True,'real_provider_used':False,'telegram_sender_used':False,'durable_legacy_payment_dispatch_verified':True,'shared_core_lifecycle_verified':False,'protected_copy_rehearsal_verified':False,'step3_complete':False,'task_complete':False,'release_ready':False,'source_sha256':{n:p.digest(root/'tools'/n) for n in ['cutover_legacy_inbox.py','cutover_legacy_callback.py','cutover_inbox.py','test_cutover_legacy_inbox.py','test_cutover_legacy_inbox_runtime.py','review_cutover_legacy_inbox.py','cutover_pair.py','cutover_fence.py']},'evidence_sha256':{str(path.relative_to(root)):p.digest(path) for path in sorted([*root.glob('legacy-inbox-*-execution.private.json'),root/'legacy-inbox-result.private.json',root/'legacy-callback-audit.private.log',root/'legacy-inbox-events.private.jsonl',root/'guards-final.private.log'])}}
	p.save(root/'legacy-inbox.redacted.json',evidence);print(json.dumps(evidence,sort_keys=True))
if __name__=='__main__':
	a=argparse.ArgumentParser();a.add_argument('--root',required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
