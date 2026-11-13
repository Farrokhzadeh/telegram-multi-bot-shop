import argparse,ast,base64,hashlib,hmac,json,os,re,subprocess,uuid
from pathlib import Path
from decimal import Decimal
import cutover_legacy_telegram_inbox as t
from test_cutover_recovery_runtime import snapshot
p=t.p
f=t.f
i=t.i
if not __debug__: raise SystemExit(78)
def run(root):
	i.directory(root);assert os.geteuid()==0 and re.fullmatch(r'/var/lib/teleg-cli/t068-pair-[a-z0-9-]+',str(root));m=f.model(root);p.validate(root,m);assert f.stopped(m) and (root/'execution.exit').read_text().strip()=='0' and (root/'guards-final.exit').read_text().strip()=='0';k=t.key(root);chain=f.chain(root,m);assert chain[-1]['phase']=='frozen' and chain[-1]['target_seen']
	result=json.loads(p.private(root/'legacy-telegram-result.private.json'));assert result['actor_sigkill_exit']==137 and result['coordinator_sigkill_exit']==-9 and len(result['phases'])==5
	before=f.verify(root,json.loads(p.private(root/'legacy-telegram-before.private.json')));after=f.verify(root,json.loads(p.private(root/'legacy-telegram-after.private.json')));assert snapshot(root)==after and before['rows']['target']==after['rows']['target'] and len(after['rows']['target'])==28 and before['stock']==after['stock'] and before['rows']['legacy']==after['rows']['legacy']
	probe=json.loads(p.private(root/'transport-probe-execution.private.json'));x=p.inspect(probe['container']);assert probe['caller_exit']==probe['container_exit']==x['State']['ExitCode']==0 and not x['State']['Running'] and not x['State']['OOMKilled'] and not x['Mounts'] and x['Image']==p.LEGACY and x['HostConfig']['NetworkMode']=='none' and x['Config']['User']=='1000:1000' and probe['helper_sha256']==p.digest(root/'tools/cutover_legacy_telegram.py')
	assert probe['input_sha256']==p.digest(root/'transport-probe-input.private.py') and probe['probe_script_sha256']==p.digest(root/'probe.py')
	helper=ast.parse((root/'tools/cutover_legacy_telegram.py').read_text());main=next(n for n in helper.body if isinstance(n,ast.FunctionDef) and n.name=='main');request=next(n for n in main.body if isinstance(n,ast.ClassDef) and n.name=='LocalRequest');program=ast.parse((root/'transport-probe-input.private.py').read_text());actual=next(n for n in program.body if isinstance(n,ast.ClassDef) and n.name=='LocalRequest');assert ast.dump(request)==ast.dump(actual)
	for mode in ['invalid','dispatch','init']:
		execution=json.loads(p.private(root/('telegram-'+mode+'-execution.private.json')));x=p.inspect(execution['container']);assert not x['State']['Running'] and not x['State']['OOMKilled'] and x['State']['ExitCode']==execution['exit']==(0 if mode=='init' else 78)
	for tag,event in result['events'].items():
		records=i.read(root,event);first=records[0];raw=f.canonical(first['envelope']);assert first['signature']==hmac.new(bytes.fromhex(p.private(root/'inbox-ingress.private').strip()),raw,hashlib.sha256).hexdigest() and first['body_sha256']==hashlib.sha256(raw).hexdigest();t.verify(root,first['envelope'])
		if tag=='pending': assert len(records)==1;continue
		assert len(records)==(2 if tag=='uncertain' else 3) and records[1]['side']=='legacy';epoch=records[1]['epoch'];assert chain[epoch]['phase']=='active' and chain[epoch]['owner']=='legacy'
		execution=f.verify(root,json.loads(p.private(root/('legacy-inbox-'+event+'-execution.private.json'))));x=p.inspect(execution['container']);expected=json.loads(p.private(root/('legacy-inbox-'+event+'-configuration.private.json')));expected['Config']['Hostname']=p.inspect(m['containers']['legacy-db']['id'])['Config']['Hostname'];expected['HostConfig']['OomKillDisable']=None
		assert x['Config']==expected['Config'] and x['HostConfig']==expected['HostConfig'] and x['Image']==p.LEGACY and x['Config']['User']=='1000:1000' and not x['State']['Running'] and not x['State']['OOMKilled'] and x['State']['ExitCode']==execution['container_exit']==execution['caller_exit']==(137 if tag=='uncertain' else 0) and execution['event']==event and execution['epoch']==epoch and execution['helper_sha256']==p.digest(root/'tools/cutover_legacy_telegram.py')
		assert f.owned(root,'legacy','telegram-inputs/'+event+'.json')==t.l.payload(root,event) and execution['input_sha256']==p.digest(root/'legacy/telegram-inputs'/(event+'.json'))
		transcript=f.owned(root,'legacy','telegram-transcript-'+event+'.json');calls=transcript['calls'];apis=[c['api'] for c in calls];assert transcript['run']==m['run'] and transcript['event']==event and transcript['errors']==[] and apis==(['getMe','sendRichMessage'] if tag in {'uncertain','restarted'} else ['getMe','answerCallbackQuery'] if tag=='admin' else ['getMe','answerCallbackQuery','editMessageText'])
		for c in calls:
			if c['api'] in {'sendMessage','sendRichMessage','editMessageText'}:
				data=c['parameters'];text=data['text'] if 'text' in data else data['rich_message']['html'];assert int(data['chat_id'])==k['actor'] and isinstance(text,str) and text
			if c['api']=='editMessageText': assert int(c['parameters']['message_id'])==k['anchor']
		if tag=='uncertain': assert t.l.reconcile(root,event)['phase']=='uncertain_do_not_retry' and not (root/'legacy/telegram-results'/(event+'.json')).exists() and f.owned(root,'legacy','telegram-committed-'+event+'.json')=={'run':m['run'],'event':event}
		else:
			v=f.owned(root,'legacy','telegram-results/'+event+'.json');assert records[-1]['phase']=='result_observed' and records[-1]['outcome']=='legacy-telegram:200' and v['run']==m['run'] and v['event']==event and v['status']==200 and v['synthetic'] is True and v['release_ready'] is False and v['input_sha256']==execution['input_sha256'] and v['transport_sha256']==p.digest(root/'legacy'/('telegram-transcript-'+event+'.json')) and records[-1]['result_sha256']==p.digest(root/'legacy/telegram-results'/(event+'.json'))
	accepted=f.owned(root,'legacy','telegram-restart.json');original=f.owned(root,'legacy','telegram-before.json');intermediate=f.owned(root,'legacy','telegram-after.json');assert len(accepted)==56 and set(accepted)==set(original)==set(intermediate) and {name for name in accepted if original[name]!=accepted[name]}=={'users','user_activity'} and accepted['user_activity']['count']==original['user_activity']['count']+2 and intermediate['user_activity']['count']==original['user_activity']['count']+1
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
	audit=root/('foundation-user-audit-'+uuid.uuid4().hex);p.copy_owned(original/'legacy-db',audit);assert p.tree_fingerprint(audit)==m['source_copy_fingerprints']['legacy-db']
	query="BEGIN READ ONLY;\nSET LOCAL TIME ZONE 'UTC';\nSELECT 'T068_USER_BASE|'||replace(encode(convert_to(row_to_json(t)::text,'UTF8'),'base64'),E'\\n','')||'|'||current_setting('transaction_read_only') FROM public.users t ORDER BY id;\nROLLBACK;\n"
	id=p.call(['create','-i','--name','teleg-t068-user-review-'+uuid.uuid4().hex[:12],'--label','teleg.task=T068','--label','teleg.purpose=synthetic-user-baseline-review','--user','70:70','--network','none','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges','--cpus','.25','--memory','96m','--memory-swap','96m','--pids-limit','64','--ulimit','fsize=67108864:67108864','--tmpfs','/tmp:rw,nosuid,size=16m','--mount','type=bind,source='+str(audit)+',target=/var/lib/postgresql/data','--entrypoint','postgres',p.PG['legacy'],'--single','-D','/var/lib/postgresql/data','-c','shared_buffers=16MB','-c','default_transaction_read_only=on','t068_legacy_stage']);x=p.inspect(id);assert x['Image']==p.PG['legacy'] and x['HostConfig']['NetworkMode']=='none' and [(v['Source'],v['Destination'],v['RW']) for v in x['Mounts']]==[(str(audit),'/var/lib/postgresql/data',True)];p.save(root/'user-baseline-audit-configuration.private.json',x)
	try: baseline=subprocess.run(['docker','start','-ai',id],input=query.encode(),capture_output=True,timeout=120)
	finally:
		if p.inspect(id)['State']['Running']: p.call(['stop','--time','5',id])
	(root/'user-baseline-audit.private.log').write_bytes(baseline.stdout+baseline.stderr);x=p.inspect(id);assert baseline.returncode==x['State']['ExitCode']==0 and not x['State']['OOMKilled'];rows=re.findall(rb'T068_USER_BASE\|([A-Za-z0-9+/=]+)\|on',baseline.stdout);assert len(rows)==len(values['users'])==1
	base=json.loads(base64.b64decode(rows[0],validate=True),parse_float=Decimal);current=json.loads(values['users'][0],parse_float=Decimal);mutable={'first_name','username','is_active','last_seen_at','lang'};assert set(base)==set(current) and base['id']==current['id']==k['actor'] and {n:v for n,v in base.items() if n not in mutable}=={n:v for n,v in current.items() if n not in mutable}
	p.save(root/'user-baseline-audit-execution.private.json',{'container':id,'exit':baseline.returncode,'source_fingerprint':m['source_copy_fingerprints']['legacy-db'],'sql_sha256':hashlib.sha256(query.encode()).hexdigest(),'log_sha256':p.digest(root/'user-baseline-audit.private.log')});assert p.tree_fingerprint(original/'legacy-db')==m['source_copy_fingerprints']['legacy-db']
	def stable(x): return {'config':x['Config'],'host':x['HostConfig'],'mounts':sorted(x['Mounts'],key=lambda v:v['Destination']),'state':{k:x['State'][k] for k in ['Running','StartedAt','FinishedAt','Restarting']},'restarts':x['RestartCount'],'image':x['Image']}
	preexisting=json.loads(p.private(root/'preexisting.private.json'))
	for id,x in preexisting.items(): assert stable(p.inspect(id))==stable(x)
	running=set();starts=[]
	for line in (root/'legacy-telegram-events.private.jsonl').read_text().splitlines():
		v=json.loads(line);id=v['Actor']['ID'];assert id in {m['containers'][side+'-db']['id'] for side in f.SIDES}
		if v['Action']=='start': running.add(id);starts.append(id);assert len(running)==1
		else: assert v['Action']=='die';running.discard(id)
	assert starts==[m['containers'][side+'-db']['id'] for side in ['legacy','legacy','target']] and not running and f.stopped(m)
	guards=(root/'guards-final.private.log').read_text();assert 'Ran 50 tests' in guards and guards.rstrip().endswith('OK')
	evidence={'base_main':'6a9d3eb067974be6a4357fa3e7b897e849b9ef86','scope':'generated-data legacy PTB dispatcher, actual menu/profile handlers, staff rejection and durable fenced handoff with in-memory API transport','host_guards':50,'transport_rich_and_foreign_destination_probe_verified':True,'runtime_phases':result['phases'],'actor_sigkill_exit':137,'coordinator_sigkill_exit':-9,'independent_stopped_legacy_tables':56,'unchanged_legacy_tables':54,'unchanged_target_tables':28,'command_activity_rows_added':2,'membership_nonpresentation_columns_unchanged':True,'database_starts':3,'overlap':False,'preexisting_containers_unchanged':len(preexisting),'all_four_pair_containers_stopped':True,'original_copies_unchanged':True,'real_telegram_or_provider_used':False,'poller_or_job_worker_started':False,'legacy_dispatcher_routing_verified':True,'shared_core_lifecycle_verified':False,'protected_copy_rehearsal_verified':False,'step3_complete':False,'task_complete':False,'release_ready':False,'source_sha256':{n:p.digest(root/'tools'/n) for n in ['cutover_inbox.py','cutover_legacy_inbox.py','cutover_legacy_telegram_inbox.py','cutover_legacy_telegram.py','test_cutover_legacy_telegram.py','test_cutover_legacy_telegram_runtime.py','review_cutover_legacy_telegram.py','cutover_pair.py','cutover_fence.py']},'evidence_sha256':{str(path.relative_to(root)):p.digest(path) for path in sorted([*root.glob('legacy-inbox-*-execution.private.json'),*root.glob('telegram-*-execution.private.json'),root/'transport-probe-execution.private.json',root/'user-baseline-audit-execution.private.json',root/'legacy-telegram-result.private.json',root/'legacy-callback-audit.private.log',root/'legacy-telegram-events.private.jsonl',root/'guards-final.private.log'])}}
	p.save(root/'legacy-telegram.redacted.json',evidence);print(json.dumps(evidence,sort_keys=True))
if __name__=='__main__':
	a=argparse.ArgumentParser();a.add_argument('--root',required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
