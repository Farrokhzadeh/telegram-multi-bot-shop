import argparse,hashlib,hmac,json,os,signal,subprocess,sys,time
from pathlib import Path
import cutover_legacy_inbox as l
from test_cutover_fence_runtime import wait_for
from test_cutover_recovery_runtime import snapshot
f=l.f
p=l.p
i=l.i
def run(root):
	m=f.model(root);p.validate(root,m);s=f.chain(root,m)[-1];assert s['phase']=='frozen' and f.stopped(m);k=l.key(root);c=i.config(root);phases=[];before=snapshot(root);p.save(root/'legacy-inbox-before.private.json',f.sign(root,before))
	stream=(root/'legacy-inbox-events.private.jsonl').open('wb');errors=(root/'legacy-inbox-events-errors.private.log').open('wb');events=subprocess.Popen(['docker','events','--since',str(int(time.time())),'--filter','container='+m['containers']['legacy-db']['id'],'--filter','container='+m['containers']['target-db']['id'],'--filter','event=start','--filter','event=die','--format','{{json .}}'],stdout=stream,stderr=errors);time.sleep(.2);assert events.poll() is None
	def record(name):
		phases.append(name);p.save(root/'legacy-inbox-progress.private.json',{'phases':phases})
	def notice(status='finished',unbound=False):
		v={'order_id':'unbound-'+m['run'] if unbound else k['invoice'],'payment_status':status,'payment_id':k['provider'],'invoice_id':k['provider'],'price_amount':10.25,'price_currency':'usd','pay_currency':'usdtbsc'};raw=f.canonical(v).decode();sig=hmac.new(k['secret'].encode(),raw.encode(),hashlib.sha512).hexdigest();return {'run':m['run'],'brand':c['brand'],'channel':'payment','origin':c['origins']['payment'],'actor':c['actor'],'event':hashlib.sha256(bytes.fromhex(sig)).hexdigest(),'action':'legacy-payment','body':raw,'signature':sig}
	def capture(v):
		raw=f.canonical(v);return i.capture(root,raw,hmac.new(bytes.fromhex(p.private(root/'inbox-ingress.private').strip()),raw,hashlib.sha256).hexdigest())
	def denied(fn):
		try: fn()
		except AssertionError: return
		raise AssertionError('Unexpected legacy payment admission')
	def actor(mode,tag):
		*_,flags=p.config(root,m['run'],'legacy');flags=flags.copy();flags[1]+='-inbox-check-'+tag;index=flags.index(p.LEGACY);flags[index:index]=['--mount','type=bind,source='+str(root/'tools/cutover_legacy_callback.py')+',target=/runtime/callback.py,readonly'];flags[-2:]=['/runtime/callback.py',mode]
		id=p.call(['create',*flags]);p.save(root/('legacy-inbox-check-'+tag+'-configuration.private.json'),p.inspect(id))
		with (root/('legacy-inbox-check-'+tag+'.private.log')).open('wb') as log: result=subprocess.run(['docker','start','-a',id],stdout=log,stderr=subprocess.STDOUT,timeout=120)
		x=p.inspect(id);assert result.returncode==x['State']['ExitCode']==0 and not x['State']['OOMKilled'] and not x['State']['Running'];p.save(root/('legacy-inbox-check-'+tag+'-execution.private.json'),{'container':id,'exit':result.returncode})
	try:
		ready=notice();first=capture(ready)['event'];assert capture(ready)['duplicate'];formatted={**ready,'body':'\n'+ready['body']+'\n'};assert capture(formatted)['duplicate'];bad={**ready,'signature':'0'*128,'event':hashlib.sha256(bytes.fromhex('0'*128)).hexdigest()};denied(lambda:capture(bad));unbound=capture(notice(unbound=True))['event'];interrupted=capture(notice(status='confirming'))['event'];record('authenticated-capture-and-duplicates-with-both-writers-frozen')
		s=f.grant(root,'legacy',s['epoch']);denied(lambda:l.dispatch(root,first,s['epoch']-1));denied(lambda:i.dispatch(root,first,'legacy',s['epoch']))
		code="import os,signal,sys;from pathlib import Path;from unittest.mock import patch;import cutover_legacy_inbox as l\ncall=l.p.call\ndef crash(args,*a,**k):\n if args[0]=='create': os.kill(os.getpid(),signal.SIGKILL)\n return call(args,*a,**k)\nwith patch.object(l.p,'call',side_effect=crash): l.dispatch(Path(sys.argv[1]),sys.argv[2],int(sys.argv[3]))\n"
		with (root/'legacy-inbox-before-admission-crash.private.log').open('wb') as log: killed=subprocess.run([sys.executable,'-c',code,str(root),interrupted,str(s['epoch'])],env={**os.environ,'PYTHONPATH':str(root/'tools')},stdout=log,stderr=subprocess.STDOUT,timeout=120)
		assert killed.returncode==-signal.SIGKILL and l.reconcile(root,interrupted)['phase']=='uncertain_do_not_retry';denied(lambda:l.dispatch(root,interrupted,s['epoch']));record('sigkill-before-actor-admission-retains-uncertain-input-without-retry')
		code="import os,signal,sys;from pathlib import Path;import cutover_legacy_inbox as l\nl.dispatch(Path(sys.argv[1]),sys.argv[2],int(sys.argv[3]));os.kill(os.getpid(),signal.SIGKILL)\n"
		with (root/'legacy-inbox-after-commit-crash.private.log').open('wb') as log: committed=subprocess.run([sys.executable,'-c',code,str(root),first,str(s['epoch'])],env={**os.environ,'PYTHONPATH':str(root/'tools')},stdout=log,stderr=subprocess.STDOUT,timeout=150)
		assert committed.returncode==-signal.SIGKILL and l.reconcile(root,first)['outcome']=='legacy-payment:200';denied(lambda:l.dispatch(root,first,s['epoch']));l.dispatch(root,unbound,s['epoch']);assert l.reconcile(root,unbound)['phase']=='held';actor('review','accepted');record('actual-legacy-credit-survives-coordinator-sigkill-unbound-notice-held')
		s=f.freeze(root,s['epoch']);accepted=snapshot(root);assert before['rows']['target']==accepted['rows']['target'] and before['stock']==accepted['stock'] and before['rows']['legacy']==accepted['rows']['legacy'];s=f.grant(root,'legacy',s['epoch']);actor('replay','restart');s=f.freeze(root,s['epoch']);assert snapshot(root)==accepted;record('legacy-restart-replay-preserves-credit-and-notification-obligation')
		pending=capture(notice(status='sending'))['event'];s=f.grant(root,'target',s['epoch']);denied(lambda:l.dispatch(root,pending,s['epoch']));denied(lambda:i.dispatch(root,pending,'target',s['epoch']));assert l.reconcile(root,interrupted)['phase']=='uncertain_do_not_retry';s=f.freeze(root,s['epoch']);assert snapshot(root)==accepted;p.save(root/'legacy-inbox-after.private.json',f.sign(root,accepted));assert i.inventory(root)=={'result_observed':1,'held':1,'dispatching':1,'captured':1};record('target-handoff-rejects-legacy-admission-and-preserves-all28-target-tables')
		time.sleep(.5);events.terminate();events.wait(timeout=10);stream.close();errors.close();running=set();starts=[]
		for line in (root/'legacy-inbox-events.private.jsonl').read_text().splitlines():
			v=json.loads(line);id=v['Actor']['ID'];assert id in {m['containers'][side+'-db']['id'] for side in f.SIDES}
			if v['Action']=='start': running.add(id);starts.append(id);assert len(running)==1
			elif v['Action']=='die': running.discard(id)
		assert starts==[m['containers'][side+'-db']['id'] for side in ['legacy','legacy','target']] and not running and f.stopped(m)
		p.save(root/'legacy-inbox-result.private.json',{'run':m['run'],'phases':phases,'coordinator_sigkill_exits':[killed.returncode,committed.returncode],'database_starts':3,'overlap':False,'events':{'accepted':first,'unbound':unbound,'uncertain':interrupted,'pending':pending},'target_tables_unchanged':28,'all_four_stopped':True,'step3_complete':False,'task_complete':False,'release_ready':False})
	finally:
		p.stop(root,m)
		if events.poll() is None: events.terminate();events.wait(timeout=10)
		stream.close();errors.close()
if __name__=='__main__':
	a=argparse.ArgumentParser();a.add_argument('--root',required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
