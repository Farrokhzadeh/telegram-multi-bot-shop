import hashlib,hmac,json,os,unittest
from unittest.mock import patch
import cutover_legacy_inbox as l
import test_cutover_inbox as fixtures
i=l.i
f=l.f
p=l.p
class LegacyInboxTests(unittest.TestCase):
	envelope=fixtures.InboxTests.envelope
	signed=fixtures.InboxTests.signed
	active=fixtures.InboxTests.active
	tearDown=fixtures.InboxTests.tearDown
	def setUp(self):
		fixtures.InboxTests.setUp(self);p.save(self.root/'legacy/callback-key.json',{'run':self.m['run'],'secret':'a'*64});os.chown(self.root/'legacy/callback-key.json',1000,1000);(self.root/'tools').mkdir();(self.root/'tools/cutover_legacy_callback.py').write_text('synthetic');l.bind(self.root)
	def notice(self,**changes):
		body=json.dumps({'order_id':'synthetic','payment_status':'finished'},sort_keys=True,separators=(',',':'));signature=hmac.new(('a'*64).encode(),body.encode(),hashlib.sha512).hexdigest()
		return self.envelope(channel='payment',action='legacy-payment',body=body,signature=signature,event=hashlib.sha256(bytes.fromhex(signature)).hexdigest(),**changes)
	def test_legacy_signature_and_duplicate_binding(self):
		v=self.notice();body,mac=self.signed(v);event=i.capture(self.root,body,mac)['event'];self.assertTrue(i.capture(self.root,body,mac)['duplicate']);v['body']='\n'+v['body']+'\n';self.assertTrue(i.capture(self.root,*self.signed(v))['duplicate']);self.assertEqual(l.payload(self.root,event)['body'],self.notice()['body'])
	def test_bad_signature_duplicate_keys_nonobject_and_nonfinite_rejected(self):
		for body in ['{}','[]','{"x":1,"x":2}','{"x":NaN}']:
			v=self.notice();v['body']=body
			with self.assertRaises((AssertionError,ValueError)): i.capture(self.root,*self.signed(v))
		self.assertEqual(i.inventory(self.root),{})
	def test_generic_dispatch_and_foreign_owner_do_not_claim_legacy_event(self):
		e=i.capture(self.root,*self.signed(self.notice()))['event'];epoch=self.active()
		with self.assertRaises(AssertionError): i.dispatch(self.root,e,'target',epoch)
		with self.assertRaises(AssertionError): l.dispatch(self.root,e,epoch)
		self.assertEqual(len(i.read(self.root,e)),1)
	def test_incomplete_dispatch_stays_uncertain_without_retry(self):
		e=i.capture(self.root,*self.signed(self.notice()))['event'];i.append(self.root,e,{'phase':'dispatching','side':'legacy','epoch':1,'request':e});self.assertEqual(l.reconcile(self.root,e)['phase'],'uncertain_do_not_retry')
		with self.assertRaises(AssertionError): l.dispatch(self.root,e,1)
	def test_key_or_helper_drift_is_rejected_before_capture(self):
		(self.root/'tools/cutover_legacy_callback.py').write_text('changed')
		with self.assertRaises(AssertionError): i.capture(self.root,*self.signed(self.notice()))
if __name__=='__main__': unittest.main()
