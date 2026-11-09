import importlib.util
import pathlib
import tempfile
import unittest

p=pathlib.Path(__file__).resolve().parents[1]/"t064_history_file_audit.py"
spec=importlib.util.spec_from_file_location("t064_history",p)
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class T064PrivateHistoryFileTests(unittest.TestCase):
	def setUp(self):
		self.temp=tempfile.TemporaryDirectory()
		self.addCleanup(self.temp.cleanup)
		self.root=pathlib.Path(self.temp.name)
		self.data={}
		for source in ("prod1","prod2"):
			d=self.root/("teleg-history-archive-"+source)/"teleg-customer-history-reviewed"
			d.mkdir(parents=True,mode=0o700)
			d.parent.chmod(0o700)
			self.data[source]=d
		self.a=self.data["prod1"]/("a"*32+".history")
		self.b=self.data["prod2"]/("b"*32+".history")
		for p,body in ((self.a,b"a"*84),(self.b,b"b"*84)):
			p.write_bytes(body)
			p.chmod(0o600)
		self.counts={"prod1":1,"prod2":1}
	def test_unique_private_records_across_brands(self):
		report=module.audit(self.root,self.counts)
		self.assertEqual(2,report["total_members"])
		self.assertTrue(report["matched"])
		self.assertFalse(report["release_ready"])
	def test_duplicate_ciphertext_across_brands_rejected(self):
		self.b.write_bytes(self.a.read_bytes())
		with self.assertRaises(ValueError):module.audit(self.root,self.counts)
	def test_world_readable_file_rejected(self):
		self.a.chmod(0o644)
		with self.assertRaises(ValueError):module.audit(self.root,self.counts)
	def test_hardlinked_file_rejected(self):
		self.b.unlink()
		self.b.hardlink_to(self.a)
		with self.assertRaises(ValueError):module.audit(self.root,self.counts)
	def test_symlink_rejected(self):
		self.b.unlink()
		self.b.symlink_to(self.a)
		with self.assertRaises(ValueError):module.audit(self.root,self.counts)
	def test_unexpected_path_rejected(self):
		(self.data["prod1"]/"extra.txt").write_text("unexpected")
		with self.assertRaises(ValueError):module.audit(self.root,self.counts)
if __name__=="__main__":
	unittest.main()
