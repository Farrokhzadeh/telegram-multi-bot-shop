import argparse,fcntl,json,os,pathlib,re,signal,subprocess,time,urllib.error,urllib.request
STAGE='accabad-staging-bot-1'
def run(args,timeout=30):
	return subprocess.run(args,capture_output=True,text=True,check=True,timeout=timeout).stdout
def private(path,maximum=1048576):
	s=path.lstat()
	if not path.is_file() or path.is_symlink() or s.st_nlink!=1 or s.st_mode&0o077 or not 0<s.st_size<=maximum:raise ValueError('Private file rejected')
	return path.read_bytes()
def write_new(path,data):
	with path.open('xb') as f:f.write(data);f.flush();os.fsync(f.fileno())
def approved(update,actor,since):
	m=update.get('message',{});a=m.get('from',{});c=m.get('chat',{})
	return type(update.get('update_id')) is int and update['update_id']>0 and type(m.get('message_id')) is int and m['message_id']>0 and a.get('id')==actor and a.get('is_bot') is False and c.get('id')==actor and c.get('type')=='private' and m.get('text')=='/orders' and type(m.get('date')) is int and since<=m['date']<=int(time.time())+30
def api(token,method,values=None):
	request=urllib.request.Request('https://api.telegram.org/bot'+token+'/'+method,data=json.dumps(values or {}).encode(),headers={'Content-Type':'application/json'})
	with urllib.request.urlopen(request,timeout=25) as response:
		body=response.read(1048577)
	if len(body)>1048576:raise ValueError('Telegram response too large')
	data=json.loads(body)
	if data.get('ok') is not True:raise ValueError('Telegram request rejected')
	return data['result']
def poll(token,values):
	for attempt in range(3):
		try:return api(token,'getUpdates',values)
		except (TimeoutError,urllib.error.URLError) as error:
			if isinstance(error,urllib.error.HTTPError) or attempt==2:raise
			print('WAIT: receive timeout; retrying command reception only.',flush=True);time.sleep(1)
def runtime(args,deliver=False):
	root='/app/data/teleg-staging-history-'+args.brand+'-20261111'
	command=['docker','run','--rm','--name','teleg-t064-history-once','--network','container:'+args.database_container,'--read-only','--cap-drop','ALL','--security-opt','no-new-privileges','--cpus','0.5','--memory','512m','--pids-limit','128','--tmpfs','/tmp:rw,noexec,nosuid,size=32m','-e','INSTANCE_ID=staging','-e','DOTNET_CLI_TELEMETRY_OPTOUT=1','-e','DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1','-e','SHOP_ALLOW_STAGING_HISTORY_'+('TELEGRAM_SEND' if deliver else 'PREFLIGHT')+'=1','-v',str(args.runtime)+':/runtime:ro','-v',str(args.root)+':'+root+':ro']
	if deliver:command+=['-v',str(args.root/'teleg-history-attempts')+':'+root+'/teleg-history-attempts:rw']
	command+=['mcr.microsoft.com/dotnet/sdk:10.0','dotnet','/runtime/Platform.BotHost.dll','--staging-history-once' if deliver else '--staging-history-preflight','--root',root]
	return run(command,120)
def main():
	p=argparse.ArgumentParser();p.add_argument('--root',type=pathlib.Path,required=True);p.add_argument('--runtime',type=pathlib.Path,required=True);p.add_argument('--brand',choices=['accabad','zikotech'],required=True);p.add_argument('--database-container',choices=['teleg-t064-history-egress-pg'],required=True);p.add_argument('--listen',action='store_true');p.add_argument('--seconds',type=int,default=1800);args=p.parse_args()
	os.umask(0o077)
	if not 60<=args.seconds<=3600 or not args.root.is_absolute() or args.root.is_symlink() or args.root.stat().st_mode&0o077 or not args.runtime.is_absolute():raise ValueError('Listener configuration rejected')
	lock=(args.root.parent/'listener.lock').open('a');fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
	binding=json.loads(private(args.root/'runtime-binding.json',4096));actor=binding['actorId']
	if binding['instanceId']!='staging' or binding['botUsername']!='testadbadbot' or type(actor) is not int or actor<=0:raise ValueError('Staging binding rejected')
	stage=json.loads(run(['docker','inspect',STAGE]))[0];env=dict(x.split('=',1) for x in stage['Config']['Env'] if '=' in x)
	if env.get('INSTANCE_ID')!='staging' or stage['Config']['Labels'].get('com.docker.compose.project')!='accabad-staging' or not stage['State']['Running']:raise ValueError('Staging container rejected')
	token=env.get('BOT_TOKEN','')
	if not re.fullmatch(str(binding['botId'])+r':[A-Za-z0-9_-]{20,120}',token):raise ValueError('Token binding rejected')
	identity=api(token,'getMe');webhook=api(token,'getWebhookInfo')
	if identity.get('id')!=binding['botId'] or identity.get('username')!='testadbadbot' or webhook.get('url'):raise ValueError('Exclusive polling unavailable')
	output=runtime(args)
	if not output.startswith('PASS: isolated staging private history preflight;'):raise ValueError('Runtime preflight failed')
	print('PASS: private runtime, staging bot identity and webhook absence verified; no messages sent.',flush=True)
	if not args.listen:return 0
	if (args.root/'telegram-update.json').exists() or (args.root/'bot-token').exists() or any((args.root/'teleg-history-attempts').iterdir()):raise ValueError('Prior attempt requires manual review')
	signal.signal(signal.SIGTERM,lambda *_:exit(143));signal.signal(signal.SIGINT,lambda *_:exit(130))
	receipt={'schema_version':1,'brand':args.brand,'telegram_polling_ingress':True,'real_update_received':False,'runtime_invoked':False,'telegram_response_confirmed':False,'legacy_staging_restored':False,'automatic_send_retry':False,'ci_invoked':False}
	stopped=False
	try:
		stopped=True;run(['docker','stop','--time','20',STAGE]);since=int(time.time())+1;deadline=time.monotonic()+args.seconds;offset=None
		write_new(args.root/'bot-token',token.encode())
		write_new(args.root/'listener-ready.json',json.dumps({'ready':True,'brand':args.brand,'expires_at':int(time.time())+args.seconds,'single_request':True}).encode())
		print('READY: waiting for one fresh approved private /orders command.',flush=True)
		while time.monotonic()<deadline:
			values={'timeout':15,'limit':100,'allowed_updates':['message']}
			if offset is not None:values['offset']=offset
			updates=poll(token,values)
			if not isinstance(updates,list):raise ValueError('Polling response rejected')
			for update in updates:
				uid=update.get('update_id')
				if type(uid) is not int or uid<0:raise ValueError('Update identity rejected')
				offset=max(offset or 0,uid+1)
				if not approved(update,actor,since):continue
				receipt['real_update_received']=True;write_new(args.root/'telegram-update.json',json.dumps(update,separators=(',',':')).encode());receipt['runtime_invoked']=True
				output=runtime(args,True)
				if not output.startswith('PASS: isolated authenticated private historical response confirmed;'):raise ValueError('Runtime result uncertain')
				receipt['telegram_response_confirmed']=True
				api(token,'getUpdates',{'offset':uid+1,'timeout':0,'limit':1,'allowed_updates':['message']})
				print('PASS: one real private history response confirmed; no automatic resend.',flush=True)
				return 0
		print('STOP: listener expired without an approved command.',flush=True);return 2
	finally:
		if stopped:
			if receipt['runtime_invoked']:subprocess.run(['docker','stop','--time','10','teleg-t064-history-once'],capture_output=True,timeout=20)
			run(['docker','start',STAGE]);receipt['legacy_staging_restored']=True
		(args.root/'listener-ready.json').unlink(missing_ok=True);(args.root/'bot-token').unlink(missing_ok=True)
		write_new(args.root/'listener-result.json',json.dumps(receipt,sort_keys=True).encode())
if __name__=='__main__':
	try:raise SystemExit(main())
	except Exception as error:print('STOP: private history listener rejected or outcome uncertain; type='+type(error).__name__+'; http_status='+str(getattr(error,'code',None))+'; inspect private markers before any retry.',flush=True);raise SystemExit(78)
