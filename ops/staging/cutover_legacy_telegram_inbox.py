import argparse,hmac,json,os,re,stat,sys
from pathlib import Path
import cutover_legacy_inbox as l
i=l.i
f=l.f
p=l.p
if not __debug__: raise SystemExit(78)
def key(root):
	m=f.model(root);v=f.verify(root,json.loads(p.private(root/'legacy-telegram-binding.private.json')));k=f.owned(root,'legacy','telegram-key.json');state=f.owned(root,'legacy','state.json')
	assert k['run']==m['run'] and k['actor']==state['user'] and type(k['bot']) is int and k['bot']>0 and type(k['anchor']) is int and k['anchor']>0 and re.fullmatch('[a-f0-9]{64}',k['secret']) and v=={'run':m['run'],'manifest':f.digest(m),'key_sha256':p.digest(root/'legacy/telegram-key.json'),'helper_sha256':p.digest(root/'tools/cutover_legacy_telegram.py')}
	for name in ['telegram-inputs','telegram-results']:
		path=root/'legacy'/name;s=path.lstat();assert path.resolve(strict=True)==path and stat.S_ISDIR(s.st_mode) and s.st_uid==1000 and stat.S_IMODE(s.st_mode)==0o700
	return k

def bind(root):
	with i.lock(root),f.lock(root):
		i.config(root);m=f.model(root);assert f.chain(root,m)[-1]['phase']=='frozen' and f.stopped(m) and not (root/'legacy-telegram-binding.private.json').exists()
		for name in ['telegram-inputs','telegram-results']: p.directory(root/'legacy'/name,1000)
		p.save(root/'legacy-telegram-binding.private.json',f.sign(root,{'run':m['run'],'manifest':f.digest(m),'key_sha256':p.digest(root/'legacy/telegram-key.json'),'helper_sha256':p.digest(root/'tools/cutover_legacy_telegram.py')}));f.sync(root/'legacy');f.sync(root);key(root)

def guard(k,v):
	assert v['channel']=='telegram' and v['action']=='legacy-telegram' and hmac.compare_digest(v['header'],k['secret']) and 2<=len(v['body'].encode())<=2048
	def unique(pairs):
		x={}
		for name,value in pairs:
			assert name not in x;x[name]=value
		return x
	u=json.loads(v['body'],object_pairs_hook=unique);assert isinstance(u,dict) and type(u.get('update_id')) is int and str(u['update_id'])==v['event']
	assert set(u) in [{'update_id','message'},{'update_id','callback_query'}]
	callback='callback_query' in u;q=u['callback_query'] if callback else None;m=q['message'] if callback else u['message'];actor=q['from'] if callback else m['from']
	assert type(actor['id']) is int and actor['id']==k['actor'] and actor['is_bot'] is False and m['chat']['type']=='private' and type(m['chat']['id']) is int and m['chat']['id']==k['actor'] and type(m['message_id']) is int and type(m['date']) is int
	if callback: assert m['message_id']==k['anchor'] and m['date']==k['date'] and m['from']['id']==k['bot'] and m['from']['is_bot'] is True and q['data'] in {'profile','admin_panel'} and isinstance(q['id'],str) and 1<=len(q['id'])<=64
	else: assert m['text']=='/menu' and m.get('entities')==[{'type':'bot_command','offset':0,'length':5}]
	return u

def verify(root,v): return guard(key(root),v)
if __name__=='__main__':
	a=argparse.ArgumentParser();a.add_argument('action',choices=['bind','dispatch','reconcile']);a.add_argument('--root',required=True);a.add_argument('--event');a.add_argument('--epoch',type=int);args=a.parse_args();os.umask(0o077);root=Path(args.root)
	try:
		assert os.geteuid()==0 and re.fullmatch(r'/var/lib/teleg-cli/t068-pair-[a-z0-9-]+',str(root));i.directory(root)
		result=bind(root) if args.action=='bind' else l.dispatch(root,args.event,args.epoch) if args.action=='dispatch' else l.reconcile(root,args.event);print(json.dumps(result,sort_keys=True))
	except Exception: print('Synthetic legacy Telegram inbox rejected.',file=sys.stderr);raise SystemExit(78)
