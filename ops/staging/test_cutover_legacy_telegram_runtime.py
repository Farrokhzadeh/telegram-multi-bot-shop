import argparse,hashlib,hmac,json,os,signal,subprocess,sys,time
from pathlib import Path
import cutover_legacy_telegram_inbox as t
from test_cutover_fence_runtime import wait_for
from test_cutover_recovery_runtime import snapshot
l=t.l
i=t.i
f=t.f
p=t.p
def run(root):
	m=f.model(root);p.validate(root,m);s=f.chain(root,m)[-1];assert s['phase']=='frozen' and f.stopped(m);k=t.key(root);c=i.config(root);phases=[];before=snapshot(root);p.save(root/'legacy-telegram-before.private.json',f.sign(root,before))
	stream=(root/'legacy-telegram-events.private.jsonl').open('wb');errors=(root/'legacy-telegram-events-errors.private.log').open('wb');events=subprocess.Popen(['docker','events','--since',str(int(time.time())),'--filter','container='+m['containers']['legacy-db']['id'],'--filter','container='+m['containers']['target-db']['id'],'--filter','event=start','--filter','event=die','--format','{{json .}}'],stdout=stream,stderr=errors);time.sleep(.2);assert events.poll() is None
	def record(name):
		phases.append(name);p.save(root/'legacy-telegram-progress.private.json',{'phases':phases})
	def notice(n,command=False,admin=False):
		actor={'id':k['actor'],'is_bot':False,'first_name':'SYNTHETIC ONLY'};message={'message_id':k['anchor'],'date':k['date'],'chat':{'id':k['actor'],'type':'private'},'from':{'id':k['bot'],'is_bot':True,'first_name':'SYNTHETIC BOT'}}
		u={'update_id':n,'message':{**message,'from':actor,'text':'/menu','entities':[{'type':'bot_command','offset':0,'length':5}]}} if command else {'update_id':n,'callback_query':{'id':'synthetic-'+str(n),'from':actor,'chat_instance':'synthetic','data':'admin_panel' if admin else 'profile','message':message}}
		return {'run':m['run'],'brand':c['brand'],'channel':'telegram','origin':c['origins']['telegram'],'actor':c['actor'],'event':str(n),'action':'legacy-telegram','body':f.canonical(u).decode(),'header':k['secret']}
	def capture(v):
		raw=f.canonical(v);return i.capture(root,raw,hmac.new(bytes.fromhex(p.private(root/'inbox-ingress.private').strip()),raw,hashlib.sha256).hexdigest())
	def denied(fn):
		try: fn()
		except AssertionError: return
		raise AssertionError('Unexpected legacy Telegram admission')
	def actor_snapshot(tag):
		*_,flags=p.config(root,m['run'],'legacy');flags=flags.copy();flags[1]+='-telegram-snapshot-'+tag;index=flags.index(p.LEGACY);flags[index:index]=['--mount','type=bind,source='+str(root/'tools/cutover_legacy_telegram.py')+',target=/runtime/callback.py,readonly'];flags[-2:]=['/runtime/callback.py','snapshot',tag];id=p.call(['create',*flags]);p.save(root/('telegram-snapshot-'+tag+'-configuration.private.json'),p.inspect(id))
		with (root/('telegram-snapshot-'+tag+'.private.log')).open('wb') as log: result=subprocess.run(['docker','start','-a',id],stdout=log,stderr=subprocess.STDOUT,timeout=120)
		x=p.inspect(id);assert result.returncode==x['State']['ExitCode']==0 and not x['State']['OOMKilled'];p.save(root/('telegram-snapshot-'+tag+'-execution.private.json'),{'container':id,'exit':result.returncode})
	try:
		first_notice=notice(1,command=True);uncertain=capture(first_notice)['event'];assert capture(first_notice)['duplicate'];profile=capture(notice(2))['event'];admin=capture(notice(3,admin=True))['event'];bad={**notice(99),'header':'0'*64};denied(lambda:capture(bad));record('signed-command-and-callback-capture-with-both-writers-frozen')
		s=f.grant(root,'legacy',s['epoch']);denied(lambda:l.dispatch(root,profile,s['epoch']-1));denied(lambda:i.dispatch(root,profile,'legacy',s['epoch']));p.save(root/'legacy/telegram-pause-event.json',{'run':m['run'],'event':uncertain});os.chown(root/'legacy/telegram-pause-event.json',1000,1000)
		code="import sys;from pathlib import Path;import cutover_legacy_inbox as l\nl.dispatch(Path(sys.argv[1]),sys.argv[2],int(sys.argv[3]))\n"
		with (root/'telegram-actor-crash.private.log').open('wb') as log:
			parent=subprocess.Popen([sys.executable,'-c',code,str(root),uncertain,str(s['epoch'])],env={**os.environ,'PYTHONPATH':str(root/'tools')},stdout=log,stderr=subprocess.STDOUT)
			def marker():
				if parent.poll() is not None: raise RuntimeError('Actor terminated before durable transport marker')
				return (root/'legacy'/('telegram-committed-'+uncertain+'.json')).is_file()
			try:
				wait_for(marker);config=json.loads(p.private(root/('legacy-inbox-'+uncertain+'-configuration.private.json')));id=config['Id'];assert p.inspect(id)['State']['Running'];p.call(['kill','--signal','KILL',id]);assert parent.wait(timeout=30)!=0
			finally:
				if parent.poll() is None: parent.kill();parent.wait(timeout=10)
		execution=f.verify(root,json.loads(p.private(root/('legacy-inbox-'+uncertain+'-execution.private.json'))));assert execution['container_exit']==137 and execution['oom'] is False and l.reconcile(root,uncertain)['phase']=='uncertain_do_not_retry';denied(lambda:l.dispatch(root,uncertain,s['epoch']));record('actual-dispatcher-transport-effect-survives-actor-sigkill-without-blind-resend')
		code="import os,signal,sys;from pathlib import Path;import cutover_legacy_inbox as l\nl.dispatch(Path(sys.argv[1]),sys.argv[2],int(sys.argv[3]));os.kill(os.getpid(),signal.SIGKILL)\n"
		with (root/'telegram-coordinator-crash.private.log').open('wb') as log: killed=subprocess.run([sys.executable,'-c',code,str(root),profile,str(s['epoch'])],env={**os.environ,'PYTHONPATH':str(root/'tools')},stdout=log,stderr=subprocess.STDOUT,timeout=150)
		assert killed.returncode==-signal.SIGKILL and l.reconcile(root,profile)['outcome']=='legacy-telegram:200';l.dispatch(root,admin,s['epoch']);assert l.reconcile(root,admin)['outcome']=='legacy-telegram:200';actor_snapshot('after');record('profile-router-result-survives-coordinator-sigkill-and-core-rejects-staff-route')
		s=f.freeze(root,s['epoch']);accepted=snapshot(root);assert before['rows']['target']==accepted['rows']['target'] and before['stock']==accepted['stock'] and before['rows']['legacy']==accepted['rows']['legacy'];s=f.grant(root,'legacy',s['epoch']);assert capture(notice(2))['duplicate'];denied(lambda:l.dispatch(root,profile,s['epoch']));restarted=capture(notice(4,command=True))['event'];l.dispatch(root,restarted,s['epoch']);assert l.reconcile(root,restarted)['outcome']=='legacy-telegram:200';actor_snapshot('restart');s=f.freeze(root,s['epoch']);final=snapshot(root);assert final==accepted;record('legacy-restart-suppresses-replay-and-routes-one-new-command')
		pending=capture(notice(5))['event'];s=f.grant(root,'target',s['epoch']);denied(lambda:l.dispatch(root,pending,s['epoch']));denied(lambda:i.dispatch(root,pending,'target',s['epoch']));assert l.reconcile(root,uncertain)['phase']=='uncertain_do_not_retry';s=f.freeze(root,s['epoch']);assert snapshot(root)==final;p.save(root/'legacy-telegram-after.private.json',f.sign(root,final));assert i.inventory(root)=={'dispatching':1,'result_observed':3,'captured':1};record('target-ownership-fences-legacy-dispatch-and-retains-uncertain-input')
		time.sleep(.5);events.terminate();events.wait(timeout=10);stream.close();errors.close();running=set();starts=[]
		for line in (root/'legacy-telegram-events.private.jsonl').read_text().splitlines():
			x=json.loads(line);id=x['Actor']['ID'];assert id in {m['containers'][side+'-db']['id'] for side in f.SIDES}
			if x['Action']=='start': running.add(id);starts.append(id);assert len(running)==1
			elif x['Action']=='die': running.discard(id)
		assert starts==[m['containers'][side+'-db']['id'] for side in ['legacy','legacy','target']] and not running and f.stopped(m)
		p.save(root/'legacy-telegram-result.private.json',{'run':m['run'],'phases':phases,'actor_sigkill_exit':137,'coordinator_sigkill_exit':killed.returncode,'events':{'uncertain':uncertain,'profile':profile,'admin':admin,'restarted':restarted,'pending':pending},'database_starts':3,'overlap':False,'all_four_stopped':True,'step3_complete':False,'task_complete':False,'release_ready':False})
	finally:
		p.stop(root,m)
		if events.poll() is None: events.terminate();events.wait(timeout=10)
		stream.close();errors.close()
if __name__=='__main__':
	a=argparse.ArgumentParser();a.add_argument('--root',required=True);args=a.parse_args();os.umask(0o077);run(Path(args.root))
