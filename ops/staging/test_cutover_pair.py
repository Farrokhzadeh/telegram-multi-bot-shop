import copy,importlib.util,json,os,stat,subprocess,sys,tempfile,unittest
from pathlib import Path
from unittest.mock import patch

def module(name,file):
	s=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
pair=module("cutover_pair","cutover_pair.py")
legacy=module("cutover_legacy_runtime","cutover_legacy_runtime.py")

class PrivateInputTests(unittest.TestCase):
	def setUp(self):
		self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name);self.file=self.root/"input";self.file.write_text("status\n");self.file.chmod(0o600)
	def tearDown(self): self.tmp.cleanup()
	def test_optimized_launcher_fails_closed(self):
		x=subprocess.run([sys.executable,"-O",str(Path(__file__).with_name("cutover_pair.py")),"--help"],capture_output=True)
		self.assertEqual(x.returncode,78)
	def test_repeated_reads_allow_access_time_change(self):
		os.utime(self.file,(1,1));self.assertEqual(legacy.private(self.file),"status\n");self.assertEqual(legacy.private(self.file),"status\n")
	def test_links_modes_types_and_size_are_rejected(self):
		for mode in [0o644,0o660]:
			self.file.chmod(mode)
			with self.assertRaises(ValueError): legacy.private(self.file)
		self.file.chmod(0o600)
		link=self.root/"hard";os.link(self.file,link)
		with self.assertRaises(ValueError): legacy.private(self.file)
		link.unlink();link.symlink_to(self.file)
		with self.assertRaises(ValueError): legacy.private(link)
		fifo=self.root/"fifo";os.mkfifo(fifo,0o600)
		with self.assertRaises(ValueError): legacy.private(fifo)
		with self.assertRaises(ValueError): legacy.private(self.file,2)
	def test_publication_is_exclusive(self):
		dest=self.root/"result";legacy.write(dest,{"ready":True})
		with self.assertRaises(FileExistsError): legacy.write(dest,{"ready":False})
		self.assertTrue(json.loads(dest.read_text())["ready"])

class PairGuardTests(unittest.TestCase):
	def setUp(self):
		self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name);self.run="1"*32
		(self.root/"build/bin").mkdir(parents=True);(self.root/"build/bin/app").write_bytes(b"public synthetic")
		(self.root/"tools").mkdir();(self.root/"tools/cutover_legacy_runtime.py").write_text("public fixture")
		self.m={"run":self.run,"containers":{},"bundle":{"app":pair.digest(self.root/"build/bin/app")},"legacy_script":pair.digest(self.root/"tools/cutover_legacy_runtime.py")}
		self.items={}
		for role in pair.CAPS:
			name,image,uid,memory,mounts,flags=pair.config(self.root,self.run,role)
			d=self.root/role;d.mkdir(mode=0o700);os.chown(d,uid,uid)
			f=self.root/(role+".env");f.write_text("SYNTHETIC=yes\n");f.chmod(0o600)
			self.m["containers"][role]={"id":name}
			cmd=["-c","shared_buffers=16MB","-c","max_connections=16","-c","work_mem=1MB","-c","maintenance_work_mem=16MB"] if role.endswith("-db") else ["/runtime/legacy.py","serve"] if role=="legacy" else ["/runtime/Platform.AotSmoke.dll","--cutover-stage","--root","/stage/target","--mode","serve"]
			self.items[name]={"Id":name,"Name":"/"+name,"Image":image,"Config":{"User":str(uid)+":"+str(uid),"Labels":{"teleg.run":self.run,"teleg.task":"T068","teleg.role":role},"Env":["SYNTHETIC=yes"],"Cmd":cmd,"Entrypoint":["python3"] if role=="legacy" else ["dotnet"]},"HostConfig":{"ReadonlyRootfs":True,"Privileged":False,"PortBindings":{},"CapDrop":["ALL"],"Memory":memory,"MemorySwap":memory,"PidsLimit":96,"RestartPolicy":{"Name":"no"},"SecurityOpt":["no-new-privileges"],"Ulimits":[{"Name":"fsize","Hard":67108864,"Soft":67108864}],"NetworkMode":"none" if role.endswith("-db") else "container:"+pair.config(self.root,self.run,role+"-db")[0]},"Mounts":[{"Source":a,"Destination":b,"RW":c} for a,b,c in mounts],"NetworkSettings":{"Networks":{"none":{}}}}
	def tearDown(self): self.tmp.cleanup()
	def validate(self,items=None):
		with patch.object(pair,"inspect",side_effect=lambda name:(items or self.items)[name]): pair.validate(self.root,self.m)
	def test_exact_contract_and_empty_directories_pass(self): self.validate()
	def test_mutated_runtime_contracts_are_rejected(self):
		name=self.m["containers"]["target"]["id"]
		changes=[("Image","wrong"),("Name","/production"),("Config.User","0:0"),("Config.Cmd",["--qualify"]),("Config.Env",["SYNTHETIC=no"]),("HostConfig.Privileged",True),("HostConfig.Memory",1),("HostConfig.PortBindings",{"1/tcp":[{"HostPort":"1"}]}),("HostConfig.NetworkMode","host"),("HostConfig.RestartPolicy.Name","always"),("HostConfig.Ulimits",[]),("Mounts",[{"Source":"/production","Destination":"/data","RW":True}])]
		for field,value in changes:
			with self.subTest(field=field):
				items=copy.deepcopy(self.items);p=items[name];parts=field.split(".")
				for key in parts[:-1]: p=p[key]
				p[parts[-1]]=value
				with self.assertRaises(AssertionError): self.validate(items)
	def test_trust_authentication_policy_is_rejected(self):
		path=pair.hba_path(self.root,"legacy");path.write_text(pair.HBA);path.chmod(0o600);os.chown(path,70,70);self.validate()
		path.write_text("local all all trust\n")
		with self.assertRaises(AssertionError): self.validate()
	def test_added_database_network_is_rejected(self):
		items=copy.deepcopy(self.items);name=self.m["containers"]["legacy-db"]["id"];items[name]["NetworkSettings"]["Networks"]["bridge"]={}
		with self.assertRaises(AssertionError): self.validate(items)
	def test_changed_bundle_and_private_env_permissions_are_rejected(self):
		file=self.root/"build/bin/app";file.write_bytes(b"changed")
		with self.assertRaises(AssertionError): self.validate()
		file.write_bytes(b"public synthetic");(self.root/"target.env").chmod(0o644)
		with self.assertRaises(ValueError): self.validate()
	def test_request_is_complete_and_owned_before_atomic_publication(self):
		side=self.root/"legacy";(side/"requests").mkdir();(side/"results").mkdir();key="2"*32
		original=os.rename
		def publish(source,destination):
			self.assertFalse(destination.exists());self.assertEqual(source.read_text(),"status\n");self.assertEqual(source.stat().st_uid,1000);self.assertEqual(source.stat().st_nlink,1)
			original(source,destination);(side/"results"/(key+".json")).write_text('{"ready":true}')
		with patch.object(pair.os,"rename",side_effect=publish):
			self.assertEqual(pair.request(self.root,"legacy","status",key),(key,{"ready":True}))
		self.assertEqual(pair.request(self.root,"legacy","status",key),(key,{"ready":True}))
		with self.assertRaises(AssertionError): pair.request(self.root,"legacy","wallet",key)
	def test_independent_stop_survives_bundle_drift(self):
		(self.root/"build/bin/app").write_bytes(b"changed")
		with patch.object(pair,"inspect",side_effect=lambda name:self.items[name]),patch.object(pair,"call",return_value="") as calls:
			pair.stop(self.root,self.m)
			self.assertEqual(calls.call_count,4)
	def test_partial_manifest_cannot_start_databases(self):
		self.m["containers"].pop("target")
		with self.assertRaises(AssertionError): pair.start(self.root,self.m)
if __name__=="__main__": unittest.main()
