import json,os,subprocess,sys,unittest
import cutover_legacy_telegram_inbox as t
import test_cutover_inbox as fixtures
i=t.i
f=t.f
p=t.p
class LegacyTelegramTests(unittest.TestCase):
	envelope=fixtures.InboxTests.envelope
	signed=fixtures.InboxTests.signed
	active=fixtures.InboxTests.active
	tearDown=fixtures.InboxTests.tearDown
	def setUp(self):
		fixtures.InboxTests.setUp(self);self.k={'run':self.m['run'],'actor':7001,'bot':734000001,'anchor':101,'date':1700000000,'secret':'a'*64};(self.root/'tools').mkdir();(self.root/'tools/cutover_legacy_telegram.py').write_text('synthetic')
		for name,value in [('telegram-key.json',self.k),('state.json',{'user':7001})]: p.save(self.root/'legacy'/name,value);os.chown(self.root/'legacy'/name,1000,1000)
		t.bind(self.root)
	def update(self,command=False):
		m={'message_id':101,'date':self.k['date'],'chat':{'id':7001,'type':'private'},'from':{'id':734000001,'is_bot':True,'first_name':'SYNTHETIC BOT'}}
		if command: return {'update_id':1,'message':{**m,'from':{'id':7001,'is_bot':False,'first_name':'SYNTHETIC ONLY'},'text':'/menu','entities':[{'type':'bot_command','offset':0,'length':5}]}}
		return {'update_id':1,'callback_query':{'id':'synthetic','from':{'id':7001,'is_bot':False,'first_name':'SYNTHETIC ONLY'},'chat_instance':'synthetic','data':'profile','message':m}}
	def notice(self,u=None): return self.envelope(action='legacy-telegram',body=f.canonical(u or self.update()).decode(),header=self.k['secret'])
	def test_command_callback_and_exact_duplicate_are_bound(self):
		v=self.notice();first=i.capture(self.root,*self.signed(v));self.assertTrue(i.capture(self.root,*self.signed(v))['duplicate']);self.assertEqual(t.verify(self.root,self.notice(self.update(True)))['message']['text'],'/menu');self.assertEqual(t.l.payload(self.root,first['event'])['body'],v['body'])
	def test_actor_bot_chat_anchor_date_and_header_rejection(self):
		mutations=[lambda u:u['callback_query']['from'].update(id=7002),lambda u:u['callback_query']['message']['from'].update(id=2),lambda u:u['callback_query']['message']['chat'].update(type='group'),lambda u:u['callback_query']['message']['chat'].update(id=7002),lambda u:u['callback_query']['message'].update(message_id=100),lambda u:u['callback_query']['message'].update(date=1),lambda u:u.update(update_id=True)]
		for mutate in mutations:
			u=self.update();mutate(u)
			with self.assertRaises(AssertionError): i.capture(self.root,*self.signed(self.notice(u)))
		v=self.notice();v['header']='b'*64
		with self.assertRaises(AssertionError): i.capture(self.root,*self.signed(v))
		self.assertEqual(i.inventory(self.root),{})
	def test_conflicting_raw_payload_and_duplicate_json_are_not_acknowledged(self):
		v=self.notice();i.capture(self.root,*self.signed(v));v['body']='\n'+v['body']
		with self.assertRaises(AssertionError): i.capture(self.root,*self.signed(v))
		v=self.notice();v['body']='{"update_id":1,"update_id":1}'
		with self.assertRaises(AssertionError): i.capture(self.root,*self.signed(v))
	def test_wrong_owner_and_generic_dispatch_leave_capture_unclaimed(self):
		e=i.capture(self.root,*self.signed(self.notice()))['event'];epoch=self.active()
		for fn in [lambda:i.dispatch(self.root,e,'target',epoch),lambda:t.l.dispatch(self.root,e,epoch)]:
			with self.assertRaises(AssertionError): fn()
		self.assertEqual(len(i.read(self.root,e)),1)
	def test_unfinished_dispatch_remains_uncertain_and_helper_drift_rejects(self):
		e=i.capture(self.root,*self.signed(self.notice()))['event'];i.append(self.root,e,{'phase':'dispatching','side':'legacy','epoch':1,'request':e});self.assertEqual(t.l.reconcile(self.root,e)['phase'],'uncertain_do_not_retry');(self.root/'tools/cutover_legacy_telegram.py').write_text('changed')
		with self.assertRaises(AssertionError): t.key(self.root)
	def test_unsupported_command_or_callback_and_optimized_execution_rejected(self):
		for u in [self.update(),self.update(True)]:
			if 'message' in u: u['message']['text']='/admin'
			else: u['callback_query']['data']='wallet'
			with self.assertRaises(AssertionError): t.verify(self.root,self.notice(u))
		r=subprocess.run([sys.executable,'-O',t.__file__,'--help'],capture_output=True,timeout=15);self.assertEqual(r.returncode,78)
if __name__=='__main__': unittest.main()
