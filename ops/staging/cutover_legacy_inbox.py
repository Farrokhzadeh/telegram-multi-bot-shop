import argparse,hashlib,hmac,json,os,re,stat,subprocess,sys,uuid
from pathlib import Path
import cutover_inbox as i
f=i.fence
p=f.pair
if not __debug__: raise SystemExit(78)
def key(root):
	m=f.model(root);v=f.verify(root,json.loads(p.private(root/'legacy-payment-binding.private.json')));k=f.owned(root,'legacy','callback-key.json')
	assert k['run']==m['run'] and re.fullmatch('[a-f0-9]{64}',k['secret']) and v=={'run':m['run'],'manifest':f.digest(m),'key_sha256':p.digest(root/'legacy/callback-key.json'),'helper_sha256':p.digest(root/'tools/cutover_legacy_callback.py')}
	for name in ['callback-inputs','callback-results']:
		path=root/'legacy'/name;s=path.lstat();assert path.resolve(strict=True)==path and stat.S_ISDIR(s.st_mode) and s.st_uid==1000 and stat.S_IMODE(s.st_mode)==0o700
	return k

def bind(root):
	with i.lock(root),f.lock(root):
		i.config(root);m=f.model(root);assert f.chain(root,m)[-1]['phase']=='frozen' and f.stopped(m) and not (root/'legacy-payment-binding.private.json').exists()
		for name in ['callback-inputs','callback-results']: p.directory(root/'legacy'/name,1000)
		(root/'payment-duplicates').mkdir(mode=0o700,exist_ok=True);i.directory(root/'payment-duplicates')
		p.save(root/'legacy-payment-binding.private.json',f.sign(root,{'run':m['run'],'manifest':f.digest(m),'key_sha256':p.digest(root/'legacy/callback-key.json'),'helper_sha256':p.digest(root/'tools/cutover_legacy_callback.py')}));f.sync(root);key(root)

def verify(root,v):
	k=key(root)
	def unique(pairs):
		x={}
		for name,value in pairs:
			assert name not in x;x[name]=value
		return x
	value=json.loads(v['body'],object_pairs_hook=unique,parse_constant=lambda _:(_ for _ in ()).throw(ValueError()))
	assert isinstance(value,dict) and hmac.compare_digest(v['signature'].lower(),hmac.new(k['secret'].encode(),json.dumps(value,separators=(',',':'),sort_keys=True,ensure_ascii=False).encode(),hashlib.sha512).hexdigest())
	return {'body_sha256':hashlib.sha256(v['body'].encode()).hexdigest(),'key_sha256':p.digest(root/'legacy/callback-key.json'),'verified':True}

def payload(root,event):
	r=i.read(root,event)[0];v=r['envelope'];assert v['action']=='legacy-payment' and r['verification']==verify(root,v)
	return {'run':v['run'],'event':event,'body':v['body'],'signature':v['signature']}

def dispatch(root,event,epoch):
	with i.lock(root),f.lock(root):
		i.config(root);m=f.model(root);key(root);records=i.read(root,event);assert len(records)==1;v=payload(root,event);s=f.chain(root,m)[-1]
		assert s['phase']=='active' and s['owner']=='legacy' and s['epoch']==epoch;p.validate(root,m)
		assert all(p.inspect(m['containers'][role]['id'])['State']['Running']==(role in {'legacy','legacy-db'}) for role in m['containers'])
		path=root/'legacy/callback-inputs'/(event+'.json');result=root/'legacy/callback-results'/(event+'.json');assert not path.exists() and not result.exists()
		i.append(root,event,{'phase':'dispatching','side':'legacy','epoch':epoch,'request':event});p.save(path,v);os.chown(path,1000,1000);f.sync(path.parent)
		_,_,_,memory,mounts,flags=p.config(root,m['run'],'legacy');flags=flags.copy();flags[1]+='-inbox-'+event[:8];index=flags.index(p.LEGACY);flags[index:index]=['--mount','type=bind,source='+str(root/'tools/cutover_legacy_callback.py')+',target=/runtime/callback.py,readonly'];flags[-2:]=['/runtime/callback.py','dispatch',event]
		id=p.call(['create',*flags]);x=p.inspect(id);assert x['Image']==p.LEGACY and x['Config']['User']=='1000:1000' and x['Config']['Cmd']==['/runtime/callback.py','dispatch',event] and sorted((q['Source'],q['Destination'],q['RW']) for q in x['Mounts'])==sorted(mounts+[(str(root/'tools/cutover_legacy_callback.py'),'/runtime/callback.py',False)])
		h=x['HostConfig'];assert h['ReadonlyRootfs'] and h['CapDrop']==['ALL'] and not h['Privileged'] and not h['PortBindings'] and h['Memory']==h['MemorySwap']==memory and h['PidsLimit']==96 and h['RestartPolicy']['Name']=='no' and 'no-new-privileges' in h['SecurityOpt'] and h['Ulimits']==[{'Name':'fsize','Hard':67108864,'Soft':67108864}] and h['NetworkMode'] in {'container:'+m['containers']['legacy-db']['id'],'container:'+p.config(root,m['run'],'legacy-db')[0]}
		p.save(root/('legacy-inbox-'+event+'-configuration.private.json'),x)
		try:
			with (root/('legacy-inbox-'+event+'.private.log')).open('wb') as log: executed=subprocess.run(['docker','start','-a',id],stdout=log,stderr=subprocess.STDOUT,timeout=90)
		finally:
			if p.inspect(id)['State']['Running']: p.call(['stop','--time','5',id])
		x=p.inspect(id);p.save(root/('legacy-inbox-'+event+'-execution.private.json'),f.sign(root,{'event':event,'epoch':epoch,'container':id,'caller_exit':executed.returncode,'container_exit':x['State']['ExitCode'],'oom':x['State']['OOMKilled'],'input_sha256':p.digest(path),'helper_sha256':p.digest(root/'tools/cutover_legacy_callback.py')}));assert executed.returncode==x['State']['ExitCode']==0 and not x['State']['OOMKilled']
		return {'event':event,'phase':'dispatching'}

def reconcile(root,event):
	with i.lock(root):
		i.config(root);key(root);records=i.read(root,event);last=records[-1]
		assert records[0]['envelope']['action']=='legacy-payment'
		if last['phase']!='dispatching': return {'event':event,'phase':last['phase']}
		assert last['side']=='legacy' and last['request']==event
		path=root/'legacy/callback-results'/(event+'.json');execution=root/('legacy-inbox-'+event+'-execution.private.json')
		if not path.exists() or not execution.exists(): return {'event':event,'phase':'uncertain_do_not_retry'}
		x=f.verify(root,json.loads(p.private(execution)));v=f.owned(root,'legacy','callback-results/'+event+'.json');assert x['event']==event and x['epoch']==last['epoch'] and x['caller_exit']==x['container_exit']==0 and x['oom'] is False and x['helper_sha256']==p.digest(root/'tools/cutover_legacy_callback.py')
		input=root/'legacy/callback-inputs'/(event+'.json');assert f.owned(root,'legacy','callback-inputs/'+event+'.json')==payload(root,event) and v['input_sha256']==x['input_sha256']==p.digest(input) and v['run']==f.model(root)['run'] and v['event']==event and v['synthetic'] is True and v['release_ready'] is False and type(v['status']) is int and v['status'] in {200,400,403}
		return i.append(root,event,{'phase':'result_observed' if v['status']==200 else 'held','side':'legacy','epoch':last['epoch'],'result_sha256':p.digest(path),'outcome':'legacy-payment:'+str(v['status']),**({'reason':'legacy_payment_requires_review'} if v['status']!=200 else {})})

if __name__=='__main__':
	a=argparse.ArgumentParser();a.add_argument('action',choices=['bind','dispatch','reconcile']);a.add_argument('--root',required=True);a.add_argument('--event');a.add_argument('--epoch',type=int);args=a.parse_args();os.umask(0o077);root=Path(args.root)
	try:
		assert os.geteuid()==0 and re.fullmatch(r'/var/lib/teleg-cli/t068-pair-[a-z0-9-]+',str(root));i.directory(root)
		result=bind(root) if args.action=='bind' else dispatch(root,args.event,args.epoch) if args.action=='dispatch' else reconcile(root,args.event);print(json.dumps(result,sort_keys=True))
	except Exception: print('Synthetic legacy inbox rejected.',file=sys.stderr);raise SystemExit(78)
