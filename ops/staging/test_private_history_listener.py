import importlib.util,pathlib,tempfile,time,unittest
spec=importlib.util.spec_from_file_location('listener',pathlib.Path(__file__).with_name('private_history_listener.py'));listener=importlib.util.module_from_spec(spec);spec.loader.exec_module(listener)
class ListenerTests(unittest.TestCase):
	def update(self):return {'update_id':2,'message':{'message_id':3,'date':int(time.time()),'from':{'id':101,'is_bot':False},'chat':{'id':101,'type':'private'},'text':'/orders'}}
	def test_fresh_private_owner_command(self):self.assertTrue(listener.approved(self.update(),101,int(time.time())-1))
	def test_reject_foreign_actor(self):self.assertFalse(listener.approved(self.update(),102,0))
	def test_reject_forwarded_chat(self):
		u=self.update();u['message']['chat']['id']=102;self.assertFalse(listener.approved(u,101,0))
	def test_reject_group(self):
		u=self.update();u['message']['chat']['type']='group';self.assertFalse(listener.approved(u,101,0))
	def test_reject_bot(self):
		u=self.update();u['message']['from']['is_bot']=True;self.assertFalse(listener.approved(u,101,0))
	def test_reject_stale_update(self):self.assertFalse(listener.approved(self.update(),101,int(time.time())+2))
	def test_reject_other_commands(self):
		for text in ['/start','/orders 20','/orders@otherbot']:
			u=self.update();u['message']['text']=text;self.assertFalse(listener.approved(u,101,0))
	def test_reject_callback(self):self.assertFalse(listener.approved({'update_id':2,'callback_query':{}},101,0))
	def test_private_file_and_no_overwrite(self):
		with tempfile.TemporaryDirectory() as d:
			p=pathlib.Path(d)/'update';listener.write_new(p,b'one');p.chmod(0o600);self.assertEqual(listener.private(p),b'one')
			with self.assertRaises(FileExistsError):listener.write_new(p,b'two')
			p.chmod(0o644)
			with self.assertRaises(ValueError):listener.private(p)
	def test_reject_linked_file(self):
		with tempfile.TemporaryDirectory() as d:
			p=pathlib.Path(d)/'data';listener.write_new(p,b'one');p.chmod(0o600);q=pathlib.Path(d)/'link';q.symlink_to(p)
			with self.assertRaises(ValueError):listener.private(q)
if __name__=='__main__':unittest.main()
