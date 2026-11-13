import argparse,hashlib,json,os,re,subprocess,uuid,xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime,timedelta
from pathlib import Path
import cutover_pair as p
if not __debug__: raise SystemExit(78)
CHANGED=['src/Platform.Persistence/BrandWriterStore.cs','src/Platform.Persistence/OutboxStore.cs','tests/Platform.IntegrationTests/BrandWriterTests.cs']
BASE='b36d9b0f235a5dd1d9dd0637e69be6d43aae6928'
def stamp(value): return datetime.fromisoformat(value.replace('Z','+00:00'))
def run(root):
	assert os.geteuid()==0 and re.fullmatch('/var/lib/teleg-cli/t068-shared-core-[a-z0-9-]+',str(root)) and root.resolve(strict=True)==root and root.stat().st_uid==0 and root.stat().st_mode&0o777==0o700
	assert (root/'execution.exit').read_text().strip()=='0' and (root/'build.exit').read_text().strip()==(root/'rebuild.exit').read_text().strip()=='0'
	for name in ['integration-build.private.log','unit-build.private.log']: assert '0 Warning(s)' in (root/name).read_text() and '0 Error(s)' in (root/name).read_text()
	expected=[json.loads(line) for line in p.private(root/'expected-main.private.jsonl').splitlines()];assert len(expected)==397 and len({v['path'] for v in expected})==397;unchanged=0
	for v in expected:
		file=root/'work'/v['path'];data=file.read_bytes();sha=hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
		if v['path'] not in CHANGED: assert sha==v['sha'];unchanged+=1
	assert unchanged==396
	known={v['path'] for v in expected}|set(CHANGED)
	for folder in ['src','tests']:
		for file in (root/'work'/folder).rglob('*'):
			if file.is_file() and not {'obj','bin'}.intersection(file.parts): assert str(file.relative_to(root/'work')) in known
	db=json.loads(p.private(root/'database-configuration.private.json'))['Id'];database=p.inspect(db);h=database['HostConfig'];assert database['Image']==p.PG['target'] and not database['State']['Running'] and not database['State']['OOMKilled'] and database['Config']['User']=='999:999' and h['NetworkMode']=='none' and not h['PortBindings'] and h['ReadonlyRootfs'] and h['CapDrop']==['ALL'] and h['Memory']==h['MemorySwap']==96*1048576 and [(v['Source'],v['Destination'],v['RW']) for v in database['Mounts']]==[(str(root/'database'),'/var/lib/postgresql',True)]
	auth=json.loads(p.private(root/'authentication.private.json'));assert auth['correct_password'] is auth['wrong_password_rejected'] is True and auth['database']==db
	policy=(root/'database/18/docker/pg_hba.conf').read_text();assert all(line.split()[-1] in {'scram-sha-256','reject'} for line in policy.splitlines() if line.strip() and not line.lstrip().startswith('#'))
	results={};names=[]
	for tag,count in [('unit',251),('integration',42)]:
		execution=json.loads(p.private(root/(tag+'-execution.private.json')));x=p.inspect(execution['container']);saved=json.loads(p.private(root/(tag+'-configuration.private.json')));saved['Config']['Hostname']=database['Config']['Hostname'];saved['HostConfig']['OomKillDisable']=None;assert x['Config']==saved['Config'] and x['HostConfig']==saved['HostConfig'] and x['Image']==p.SDK and x['Config']['User']=='10001:10001' and not x['State']['Running'] and not x['State']['OOMKilled'] and x['State']['ExitCode']==execution['caller_exit']==execution['container_exit']==0
		h=x['HostConfig'];assert h['NetworkMode']=='container:'+db and h['ReadonlyRootfs'] and not h['PortBindings'] and not h['Privileged'] and h['Memory']==h['MemorySwap']==640*1048576 and h['PidsLimit']==128 and h['CapDrop']==['ALL'] and h['Ulimits']==[{'Name':'fsize','Hard':67108864,'Soft':67108864}]
		binary='unit' if tag=='unit' else 'integration';assert sorted((v['Source'],v['Destination'],v['RW']) for v in x['Mounts'])==sorted([(str(root/binary),'/runtime',False),(str(root/('results-'+tag)),'/results',True),(str(root/('tmp-'+tag)),'/stage/tmp',True)])
		assert x['Config']['Entrypoint']==['dotnet'] and x['Config']['Cmd'][:2]==['/usr/share/dotnet/sdk/10.0.403/vstest.console.dll','/runtime/Platform.'+('UnitTests' if tag=='unit' else 'IntegrationTests')+'.dll']
		trx=root/('results-'+tag)/'tests.trx';assert p.digest(trx)==execution['trx_sha256'];tree=ET.parse(trx);ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};counter=tree.find('.//t:Counters',ns);rows=tree.findall('.//t:UnitTestResult',ns);assert counter is not None and int(counter.attrib['total'])==int(counter.attrib['executed'])==int(counter.attrib['passed'])==len(rows)==count and all(v.attrib['outcome']=='Passed' for v in rows) and len({v.attrib['executionId'] for v in rows})==count
		for field in ['failed','error','timeout','aborted','inconclusive','notExecuted']: assert int(counter.attrib.get(field,0))==0
		times=tree.find('t:Times',ns);assert stamp(execution['started'])-timedelta(seconds=5)<=stamp(times.attrib['start'])<=stamp(times.attrib['finish'])<=stamp(execution['finished'])+timedelta(seconds=5)
		results[tag]=count
		if tag=='integration': names=[v.attrib['testName'] for v in rows]
		for file in (root/binary).rglob('*'):
			if file.is_file(): assert file.stat().st_mtime<=stamp(execution['started']).timestamp()+1
	brand=[v for v in names if '.BrandWriterTests.' in v];assert len(brand)==6 and sum('PauseDrainsAdmittedWorkWhileOtherBrandsBuyAndClaimTheirOwnJobs' in v for v in brand)==2
	for method in ['PauseResumeIsAuditedAndOldCommandsNeverReverseNewState','LostAdmissionConnectionRollsBackLeaseAndAllowsPauseWithoutLosingItsOrder','RevokedAdministratorCannotPauseOrResume','FailedRevisionOrForeignBotAdmissionCannotCommitCheckoutOrWork']: assert sum(method in v for v in brand)==1
	reused=json.loads(p.private(root/'reused-unit-artifacts.private.json'));assert reused['files']=={str(q.relative_to(root/'unit')):p.digest(q) for q in (root/'unit').rglob('*') if q.is_file()} and reused['trx_sha256']==p.digest(root/'results-unit/tests.trx')
	query="BEGIN READ ONLY;\nSELECT 'T068_SHARED_CLEANUP|'||count(*)||'|'||current_setting('transaction_read_only') FROM pg_database WHERE datname LIKE 'shop_qualification_%';\nROLLBACK;\n"
	id=p.call(['create','-i','--name','teleg-t068-shared-review-'+uuid.uuid4().hex[:12],'--label','teleg.task=T068','--label','teleg.purpose=synthetic-shared-core-review','--user','999:999','--network','none','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges','--cpus','.25','--memory','96m','--memory-swap','96m','--pids-limit','64','--ulimit','fsize=67108864:67108864','--tmpfs','/tmp:rw,nosuid,size=16m','--mount','type=bind,source='+str(root/'database')+',target=/var/lib/postgresql','--entrypoint','postgres',p.PG['target'],'--single','-D','/var/lib/postgresql/18/docker','-c','shared_buffers=16MB','-c','default_transaction_read_only=on','shop_test_control']);p.save(root/'review-database-configuration.private.json',p.inspect(id))
	try: check=subprocess.run(['docker','start','-ai',id],input=query.encode(),capture_output=True,timeout=120)
	finally:
		if p.inspect(id)['State']['Running']: p.call(['stop','--time','5',id])
	(root/'review-database.private.log').write_bytes(check.stdout+check.stderr);x=p.inspect(id);assert check.returncode==x['State']['ExitCode']==0 and not x['State']['Running'] and not x['State']['OOMKilled'] and re.findall(rb'T068_SHARED_CLEANUP\|(\d+)\|(on|off)',check.stdout)==[(b'0',b'on')];p.save(root/'review-database-execution.private.json',{'container':id,'exit':check.returncode,'query_sha256':hashlib.sha256(query.encode()).hexdigest(),'log_sha256':p.digest(root/'review-database.private.log')})
	def stable(x): return {'config':x['Config'],'host':x['HostConfig'],'mounts':sorted(x['Mounts'],key=lambda v:v['Destination']),'state':{k:x['State'][k] for k in ['Running','StartedAt','FinishedAt','Restarting']},'restarts':x['RestartCount'],'image':x['Image']}
	pre=json.loads(p.private(root/'preexisting.private.json'))
	for id,x in pre.items(): assert stable(p.inspect(id))==stable(x)
	assert not p.inspect(db)['State']['Running'];classes=Counter(re.match(r'^Platform\.IntegrationTests\.([A-Za-z_0-9]+Tests)\.',v).group(1) for v in names)
	artifacts={tag:{str(q.relative_to(root/tag)):p.digest(q) for q in (root/tag).rglob('*') if q.is_file()} for tag in ['unit','integration']};p.save(root/'artifacts.private.json',artifacts)
	evidence={'base_main':BASE,'scope':'synthetic shared-database revision-bound admission, authenticated audited pause/resume and opt-in brand-scoped job claiming','baseline_unit_cases':251,'fresh_final_unit_cases':results['unit'],'focused_postgresql_cases':results['integration'],'new_brand_writer_cases':len(brand),'source_main_inputs':397,'unchanged_main_inputs':unchanged,'classes':dict(classes),'three_brand_checkout_and_work_obligations_verified':True,'pause_drain_commit_rollback_and_backend_loss_verified':True,'schema_table_count_unchanged':28,'preexisting_containers_unchanged':len(pre),'new_database_and_test_actors_stopped':True,'fresh_offline_disposable_database_count':0,'all_host_writer_paths_fenced':False,'normal_host_activation_verified':False,'cross_database_fence_replaced':False,'protected_copy_rehearsal_verified':False,'native_aot_publication_verified':False,'step3_complete':False,'task_complete':False,'release_ready':False,'source_sha256':{v:p.digest(root/'work'/v) for v in CHANGED},'reviewer_sha256':p.digest(Path(__file__)),'evidence_sha256':{v:p.digest(root/v) for v in ['baseline.trx','format.private.log','integration-build.private.log','unit-build.private.log','results-unit/tests.trx','results-integration/tests.trx','unit-execution.private.json','integration-execution.private.json','review-database-execution.private.json','reused-unit-artifacts.private.json']},'artifact_manifest_sha256':p.digest(root/'artifacts.private.json')}
	p.save(root/'shared-brand-writer.redacted.json',evidence);print(json.dumps({'passed':True,'unit_cases':251,'integration_cases':42,'new_cases':6,'preexisting_unchanged':len(pre)}))
if __name__=='__main__':
	a=argparse.ArgumentParser();a.add_argument('--root',required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
