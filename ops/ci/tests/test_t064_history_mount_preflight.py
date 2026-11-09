import importlib.util
import pathlib
import tempfile
import unittest

path=pathlib.Path(__file__).resolve().parents[1]/"t064_history_mount_preflight.py"
spec=importlib.util.spec_from_file_location("history_mount_preflight",path)
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class CustomerHistoryMountPreflightTests(unittest.TestCase):
	def setUp(self):
		self.temp=tempfile.TemporaryDirectory()
		self.addCleanup(self.temp.cleanup)
		self.root=pathlib.Path(self.temp.name)/"protected-source-history"
		self.root.mkdir(mode=0o700)
		self.data={name:{"mounts":[{"Source":"/opt/accabad-stage-data","Destination":"/app/data"}],
			"privileged":False,"running":True,"image":module.STAGE_IMAGE if name=="accabad-staging-bot-1" else "unchanged-prod"} for name in module.BOTS}
	def test_isolated_bots_do_not_expose_archives(self):
		result=module.verify(self.root,self.data)
		self.assertEqual(4,result["bot_containers"])
		self.assertEqual(4,result["inspected_mounts"])
		self.assertFalse(result["release_ready"])
	def test_direct_mount_rejected(self):
		self.data[module.BOTS[0]]["mounts"][0]["Source"]=str(self.root)
		with self.assertRaises(ValueError):module.verify(self.root,self.data)
	def test_parent_mount_rejected(self):
		self.data[module.BOTS[1]]["mounts"][0]["Source"]=str(self.root.parent)
		with self.assertRaises(ValueError):module.verify(self.root,self.data)
	def test_descendant_mount_rejected(self):
		child=self.root/"child"
		child.mkdir(mode=0o700)
		self.data[module.BOTS[2]]["mounts"][0]["Source"]=str(child)
		with self.assertRaises(ValueError):module.verify(self.root,self.data)
	def test_symlinked_source_rejected(self):
		alias=pathlib.Path(self.temp.name)/"archive-alias"
		alias.symlink_to(self.root,target_is_directory=True)
		self.data[module.BOTS[3]]["mounts"][0]["Source"]=str(alias)
		with self.assertRaises(ValueError):module.verify(self.root,self.data)
	def test_privileged_bot_rejected(self):
		self.data[module.BOTS[0]]["privileged"]=True
		with self.assertRaises(ValueError):module.verify(self.root,self.data)
	def test_wrong_staging_image_rejected(self):
		self.data[module.BOTS[3]]["image"]="real-history"
		with self.assertRaises(ValueError):module.verify(self.root,self.data)
	def test_docker_socket_mount_rejected(self):
		self.data[module.BOTS[0]]["mounts"].append({"Source":"/var/run/docker.sock","Destination":"/var/run/docker.sock"})
		with self.assertRaises(ValueError):module.verify(self.root,self.data)
	def test_inactive_bot_rejected(self):
		self.data[module.BOTS[2]]["running"]=False
		with self.assertRaises(ValueError):module.verify(self.root,self.data)
	def test_unexpected_bot_set_rejected(self):
		del self.data[module.BOTS[2]]
		with self.assertRaises(ValueError):module.verify(self.root,self.data)
	def test_insecure_protected_root_rejected(self):
		self.root.chmod(0o755)
		with self.assertRaises(ValueError):module.verify(self.root,self.data)

if __name__=="__main__":
	unittest.main()
