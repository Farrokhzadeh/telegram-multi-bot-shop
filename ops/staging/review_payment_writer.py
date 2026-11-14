import argparse,hashlib,json,os,re,subprocess,uuid,xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime,timedelta
from pathlib import Path
import cutover_pair as p
if not __debug__: raise SystemExit(78)
CHANGED=['src/Platform.Persistence/PaymentSettlementStore.cs','src/Platform.Payments/PaymentVerificationWorker.cs','tests/Platform.IntegrationTests/PaymentWriterTests.cs']
BASE='2706da7727590885399beb7352bc80ba93da3c2c'
def stamp(value): return datetime.fromisoformat(value.replace('Z','+00:00'))
def run(root,scope='worker'):
	assert scope in {'worker','origins'}
	origins=scope=='origins';changed=['src/Platform.Persistence/PaymentEvidenceStore.cs','tests/Platform.IntegrationTests/PaymentOriginWriterTests.cs'] if origins else CHANGED;inputs=400 if origins else 399;base_main='e4fb13a34ce06652f0935ce7ab73a2c1f43f1147' if origins else BASE
	pattern='/var/lib/teleg-cli/t068-payment-origins-[a-z0-9-]+' if origins else '/var/lib/teleg-cli/t068-writer-wiring-[a-z0-9-]+'
	dependency=Path(p.__file__).read_bytes();assert hashlib.sha1(b'blob '+str(len(dependency)).encode()+b'\0'+dependency).hexdigest()=='a823306e78d7fb5c7676351067f74f919b2597d6'
	assert os.geteuid()==0 and re.fullmatch(pattern,str(root)) and root.resolve(strict=True)==root and root.stat().st_uid==0 and root.stat().st_mode&0o777==0o700
	assert (root/'execution.exit').read_text().strip()=='0' and (root/'build.exit').read_text().strip()=='0'
	for name in ['integration-build.private.log','unit-build.private.log']: assert '0 Warning(s)' in (root/name).read_text() and '0 Error(s)' in (root/name).read_text()
	expected=[json.loads(line) for line in p.private(root/'expected-main.private.jsonl').splitlines()];assert len(expected)==inputs and len({v['path'] for v in expected})==inputs;unchanged=0
	for v in expected:
		file=root/'work'/v['path'];data=file.read_bytes();sha=hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
		if v['path'] not in changed: assert sha==v['sha'];unchanged+=1
	assert unchanged==(399 if origins else 397)
	known={v['path'] for v in expected}|set(changed)
	for folder in ['src','tests']:
		for file in (root/'work'/folder).rglob('*'):
			if file.is_file() and not {'obj','bin'}.intersection(file.parts): assert str(file.relative_to(root/'work')) in known
	db=json.loads(p.private(root/'database-configuration.private.json'))['Id'];database=p.inspect(db);h=database['HostConfig'];assert database['Image']==p.PG['target'] and not database['State']['Running'] and not database['State']['OOMKilled'] and database['Config']['User']=='999:999' and h['NetworkMode']=='none' and not h['PortBindings'] and h['ReadonlyRootfs'] and h['CapDrop']==['ALL'] and h['Memory']==h['MemorySwap']==160*1048576 and h['PidsLimit']==128 and [(v['Source'],v['Destination'],v['RW']) for v in database['Mounts']]==[(str(root/'database'),'/var/lib/postgresql',True)]
	assert 'max_connections=32' in database['Config']['Cmd'] and 'shared_buffers=16MB' in database['Config']['Cmd']
	auth=json.loads(p.private(root/'authentication.private.json'));assert auth['correct_password'] is auth['wrong_password_rejected'] is True and auth['database']==db
	policy=(root/'database/18/docker/pg_hba.conf').read_text();assert all(line.split()[-1] in {'scram-sha-256','reject'} for line in policy.splitlines() if line.strip() and not line.lstrip().startswith('#'))
	results={};names=[]
	for tag,count in [('unit',251),('integration',46 if origins else 92)]:
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
	brand=[v for v in names if ('.PaymentOriginWriterTests.' if origins else '.PaymentWriterTests.') in v];assert len(brand)==(11 if origins else 8)
	methods=[('TopupAndInvoiceReplayNeedFreshAdmissionAfterResume',1),('RejectedInvoiceAdmissionPoisonsAllBorrowedTransactionWrites',3),('PausedStaleAndForeignCaptureCannotReachArchive',2),('PauseDuringArchiveRetainsOriginalButRejectsEvidenceUntilFreshResume',2),('AnotherBrandCapturesWhileFirstArchiveIsPausedWithoutHoldingItsTransaction',1),('HundredScopedReplaysKeepOneNoticeAndVerificationWithoutSettlement',1),('PauseWaitsForAdmittedInvoiceTransactionAndPreservesCommittedBinding',1)] if origins else [('PauseAfterClaimRejectsRenewalAndOldRevisionAfterResume',2),('PauseDuringLookupPreventsSettlementAndFreshResumeCanReplaySafely',2),('ActiveWorkerFinishesOrRetriesWithoutChangingOtherBrands',2),('DirectSettlementRejectsPausedAndForeignBrandBeforeTransport',1),('AnotherBrandSettlesWhileClaimedBrandIsPaused',1)]
	for method,count in methods: assert sum(method in v for v in brand)==count
	baseline=root/'baseline-results/baseline.trx';base=ET.parse(baseline);counter=base.find('.//t:Counters',ns);rows=base.findall('.//t:UnitTestResult',ns);assert int(counter.attrib['total'])==int(counter.attrib['passed'])==int(counter.attrib['executed'])==len(rows)==251 and all(v.attrib['outcome']=='Passed' for v in rows) and (root/'baseline.exit').read_text().strip()=='0'
	prior=Path('/var/lib/teleg-cli/t068-writer-wiring-v2-20261114' if origins else '/var/lib/teleg-cli/t068-shared-core-20261113');old=json.loads(p.private(prior/'artifacts.private.json'))['unit'];assert old=={str(q.relative_to(prior/'unit')):p.digest(q) for q in (prior/'unit').rglob('*') if q.is_file()}
	baseline_root=Path('/var/lib/teleg-cli/t068-payment-origins-20261114') if origins else Path('/var/lib/teleg-cli/t068-writer-wiring-20261114');b=p.inspect('teleg-t068-payment-origins-baseline' if origins else 'teleg-t068-writer-wiring-baseline');h=b['HostConfig'];assert b['Image']==p.SDK and b['Config']['User']=='10001:10001' and not b['State']['Running'] and b['State']['ExitCode']==0 and not b['State']['OOMKilled'] and h['NetworkMode']=='none' and h['ReadonlyRootfs'] and h['CapDrop']==['ALL'] and h['Memory']==h['MemorySwap']==640*1048576
	assert sorted((v['Source'],v['Destination'],v['RW']) for v in b['Mounts'])==sorted([(str(prior/'unit'),'/runtime',False),(str(baseline_root/'baseline-results'),'/results',True),(str(baseline_root/'baseline-tmp'),'/stage/tmp',True)])
	assert b['Config']['Cmd'][:3]==['dotnet','/usr/share/dotnet/sdk/10.0.403/vstest.console.dll','/runtime/Platform.UnitTests.dll']
	bt=base.find('t:Times',ns);assert stamp(b['State']['StartedAt'])-timedelta(seconds=5)<=stamp(bt.attrib['start'])<=stamp(bt.attrib['finish'])<=stamp(b['State']['FinishedAt'])+timedelta(seconds=5)
	if origins:
		unit_reuse=json.loads(p.private(root/'reused-unit-build.private.json'));old_origin=Path(unit_reuse['from']);assert old_origin==Path('/var/lib/teleg-cli/t068-payment-origins-20261114')
		assert unit_reuse['files']=={str(q.relative_to(root/'unit')):p.digest(q) for q in (root/'unit').rglob('*') if q.is_file()}=={str(q.relative_to(old_origin/'unit')):p.digest(q) for q in (old_origin/'unit').rglob('*') if q.is_file()}
		assert p.digest(root/'unit-build.private.log')==p.digest(old_origin/'unit-build.private.log')==unit_reuse['log_sha256']
		for folder in ['src','tests/Shared','tests/Platform.UnitTests']:
			def sources(base): return {str(q.relative_to(base)):p.digest(q) for q in base.rglob('*') if q.is_file() and not {'obj','bin'}.intersection(q.parts)}
			assert sources(root/'work'/folder)==sources(old_origin/'work'/folder)
	if not origins:
		reuse=json.loads(p.private(root/'reused-build.private.json'));origin=Path(reuse['from']);assert origin==Path('/var/lib/teleg-cli/t068-writer-wiring-20261114') and reuse['source_unchanged'] is reuse['failed_attempt_retained'] is True
		for part in ['work','unit','integration']:
			def manifest(folder): return {str(q.relative_to(folder)):p.digest(q) for q in folder.rglob('*') if q.is_file() and not {'obj','bin'}.intersection(q.parts)}
			assert manifest(root/part)==manifest(origin/part)==reuse['manifests'][part]
		assert p.digest(root/'baseline-results/baseline.trx')==p.digest(origin/'baseline-results/baseline.trx')
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
	if origins: assert dict(classes)=={'BrandWriterTests':6,'ExternalPaymentTests':18,'PaymentWriterTests':8,'CutoverPaymentCaptureTests':3,'PaymentOriginWriterTests':11}
	logs=['baseline-results/baseline.trx','format.private.log','integration-build.private.log','unit-build.private.log','results-unit/tests.trx','results-integration/tests.trx','unit-execution.private.json','integration-execution.private.json','review-database-execution.private.json']
	if origins:
		correction=json.loads(p.private(root/'failure-correction.private.json'));assert correction['private_body_access_rejected'] is correction['public_fixture_comparison_used'] is correction['application_visibility_unchanged'] is True and correction['test_actors_started'] is False
		assert 'CS1061' in p.private(root/'failed-visibility-integration-build.private.log')
		guards=json.loads(p.private(root/'reviewer-guards.private.json'));assert guards=={'optimized_python_exit':78,'origins_rejects_worker_root':1,'worker_rejects_origins_root':1,'reviewer_sha256':p.digest(Path(__file__))}
		correction2=json.loads(p.private(root/'test-correction.private.json'));assert correction2['valid_member_preference_write_used'] is correction2['production_source_unchanged'] is True and correction2['old_suite_passed']==43 and correction2['old_suite_failed']==3
		failed=Path(correction2['from'])/'results-integration/tests.trx';assert p.digest(failed)==correction2['failed_trx_sha256'];old_tree=ET.parse(failed);old_counter=old_tree.find('.//t:Counters',ns);assert int(old_counter.attrib['total'])==46 and int(old_counter.attrib['passed'])==43 and int(old_counter.attrib['failed'])==3
		failed_rows=[v for v in old_tree.findall('.//t:UnitTestResult',ns) if v.attrib['outcome']=='Failed'];assert len(failed_rows)==3 and all('RejectedInvoiceAdmissionPoisonsAllBorrowedTransactionWrites' in v.attrib['testName'] and 'SQLSTATE 23514' in ''.join(v.itertext()) for v in failed_rows)
		logs.extend(['reused-unit-build.private.json','test-correction.private.json','failure-correction.private.json','failed-visibility-integration-build.private.log','reviewer-guards.private.json'])
	else: logs.append('reused-build.private.json')
	evidence={'base_main':base_main,'scope':'synthetic opt-in topup creation, borrowed-session invoice binding and verified payment evidence admission before/after archive' if origins else 'synthetic opt-in payment worker and settlement revision-bound admission with previously claimed leases and in-flight fake GET lookup','baseline_unit_cases':251,'fresh_final_unit_cases':results['unit'],'focused_postgresql_cases':results['integration'],'source_main_inputs':inputs,'unchanged_main_inputs':unchanged,'classes':dict(classes),'other_brand_progress_verified':True,'schema_table_count_unchanged':28,'preexisting_containers_unchanged':len(pre),'new_database_and_test_actors_stopped':True,'fresh_offline_disposable_database_count':0,'original_unscoped_apis_remain_compatible':True,'postgres_connection_limit':32,'postgres_memory_limit_mib':160,'all_host_writer_paths_fenced':False,'normal_host_activation_verified':False,'cross_database_fence_replaced':False,'protected_copy_rehearsal_verified':False,'native_aot_publication_verified':False,'step3_complete':False,'task_complete':False,'release_ready':False,'source_sha256':{v:p.digest(root/'work'/v) for v in changed},'reviewer_sha256':p.digest(Path(__file__)),'evidence_sha256':{v:p.digest(root/v) for v in logs},'artifact_manifest_sha256':p.digest(root/'artifacts.private.json')}
	if origins: evidence.update({'new_payment_origin_cases':len(brand),'retained_test_visibility_compile_failure':True,'retained_invalid_wallet_setup_failures':3,'matching_unit_build_reused_and_rerun':True,'reviewer_scope_guard_cases':3,'rejected_borrowed_transaction_cannot_commit':True,'pause_during_archive_rejects_evidence_write':True,'retained_delegate_ciphertext_roundtrip_verified':True,'admitted_invoice_transaction_drains_before_pause':True,'hundred_scoped_replays_queue_one_verification':True,'order_clock_handoff_across_pause_qualified':False,'durable_archive_crash_matrix_qualified':False})
	else: evidence.update({'new_payment_writer_cases':len(brand),'pause_after_claim_and_during_lookup_verified':True,'stale_revision_after_resume_rejected':True,'fresh_resume_preserves_once_only_fulfillment':True,'matching_build_reused_by_exact_hashes':True,'failed_16_connection_attempt_retained':True})
	p.save(root/('payment-origins.redacted.json' if origins else 'payment-writer.redacted.json'),evidence);print(json.dumps({'passed':True,'unit_cases':251,'integration_cases':results['integration'],'new_cases':len(brand),'preexisting_unchanged':len(pre)}))

if __name__=='__main__':
	a=argparse.ArgumentParser();a.add_argument('--root',required=True);a.add_argument('--scope',choices=['worker','origins'],default='worker');args=a.parse_args();os.umask(0o077);run(Path(args.root),args.scope)
