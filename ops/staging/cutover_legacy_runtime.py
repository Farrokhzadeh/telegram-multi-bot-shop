import hashlib,json,os,stat,sys,time,uuid
from pathlib import Path

ROOT=Path("/stage/legacy")
OPS={"status","wallet","wallet-paused","stop","fake_payment","fake_supplier","fake_telegram","fake_fx","fake_ai"}

def private(path,limit=4096):
	if path.resolve(strict=True)!=path or path.parent.resolve(strict=True)!=path.parent: raise ValueError()
	fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
	try:
		s=os.fstat(fd)
		if not stat.S_ISREG(s.st_mode) or s.st_nlink!=1 or s.st_uid!=os.geteuid() or stat.S_IMODE(s.st_mode)&0o077 or not 0<s.st_size<=limit: raise ValueError()
		data=os.read(fd,limit+1);after=os.fstat(fd)
		if len(data)!=s.st_size or tuple(getattr(after,k) for k in ("st_dev","st_ino","st_mode","st_nlink","st_uid","st_gid","st_size","st_mtime_ns","st_ctime_ns"))!=tuple(getattr(s,k) for k in ("st_dev","st_ino","st_mode","st_nlink","st_uid","st_gid","st_size","st_mtime_ns","st_ctime_ns")): raise ValueError()
		return data.decode("utf-8")
	finally: os.close(fd)

def write(path,value,replace=False):
	temp=path.with_name(path.name+"."+uuid.uuid4().hex+".tmp")
	fd=os.open(temp,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
	try:
		os.write(fd,(json.dumps(value,sort_keys=True)+"\n").encode());os.fsync(fd)
	finally: os.close(fd)
	if replace: os.replace(temp,path)
	else:
		os.link(temp,path);temp.unlink()

def main(mode):
	if mode not in {"init","serve","stock-init"} or os.getenv("SHOP_CUTOVER_STAGE")!="synthetic-acc-abad-v1" or ROOT.resolve(strict=True)!=ROOT or stat.S_IMODE(ROOT.stat().st_mode)!=0o700 or ROOT.stat().st_uid!=os.geteuid(): raise ValueError()
	run=uuid.UUID(hex=private(ROOT/"identity",64).strip()).hex
	if os.getenv("POSTGRES_HOST")!="127.0.0.1" or os.getenv("POSTGRES_DB")!="t068_legacy_stage" or os.getenv("INSTANCE_ID")!="t068-acc-abad" or os.getenv("DEPLOYMENT_STAGE")!="staging": raise ValueError()
	os.environ.setdefault("DJANGO_SETTINGS_MODULE","django_project.settings.base")
	import django
	django.setup()
	from django.core.management import call_command
	from django.db import connection,transaction
	from apps.common.instance import bind_database,assert_database_binding
	from apps.accounts.models import TelegramUser
	from apps.store.models import Product,InventoryItem,Order,WalletTransaction
	from apps.common.services.inventory import add_inventory_items
	from apps.common.services.checkout import purchase_item
	state_path=ROOT/"state.json"
	if mode=="init":
		if state_path.exists() or any((ROOT/"requests").iterdir()) or any((ROOT/"results").iterdir()): raise ValueError()
		call_command("migrate",interactive=False,verbosity=0)
		bind_database()
		with transaction.atomic():
			user=TelegramUser.objects.create(id=7001,first_name="SYNTHETIC ONLY",balance=100)
			product=Product.objects.create(name="SYNTHETIC ACC-ABAD LEGACY",price=10,auto_price=False)
			WalletTransaction.objects.create(id="synthetic-opening:"+run,user=user,amount=100,desc="SYNTHETIC ONLY",currency="USD")
			add_inventory_items(product,["SYNTHETIC-LEGACY-"+run+"-"+str(i) for i in range(3)])
		write(state_path,{"run":run,"product":product.pk,"user":user.pk,"database":connection.settings_dict["NAME"]})
		return
	saved=json.loads(private(state_path))
	if set(saved)!={"run","product","user","database"} or saved["run"]!=run or saved["database"]!=connection.settings_dict["NAME"]: raise ValueError()
	if mode=="stock-init":
		plan=json.loads(private(ROOT/"stock-plan.json"))
		assert set(plan)=={"run","product","unit","credential"} and uuid.UUID(plan["run"]).hex==run and plan["credential"]=="SYNTHETIC-COMMON-"+run+"-"+uuid.UUID(plan["unit"]).hex
		with transaction.atomic():
			name="SYNTHETIC COMMON "+uuid.UUID(plan["unit"]).hex
			products=list(Product.objects.filter(name=name))
			if not products:
				product=Product.objects.create(name=name,price=10,auto_price=False)
				add_inventory_items(product,[plan["credential"]])
			else: assert len(products)==1;product=products[0]
			items=list(InventoryItem.objects.filter(product=product))
			assert len(items)==1 and items[0].order_id is None and items[0].data==plan["credential"]
			with connection.cursor() as cursor:
				cursor.execute("SELECT data FROM inventory WHERE id=%s",[items[0].pk]);cipher=cursor.fetchone()[0]
		write(ROOT/"stock-binding.json",{"product":product.pk,"unit":items[0].pk,"cipher":hashlib.sha256(cipher.encode()).hexdigest(),"plain":hashlib.sha256(plan["credential"].encode()).hexdigest()},True)
		saved["product"]=product.pk;write(state_path,saved,True)
		return
	assert_database_binding()
	if TelegramUser.objects.count()!=1 or Product.objects.count()!=(2 if (ROOT/"stock-plan.json").exists() else 1): raise ValueError()
	def result(operation,outcome):
		return {"operation":operation,"outcome":outcome,"orders":Order.objects.count(),"allocated":InventoryItem.objects.filter(order__isnull=False).count(),"debits":WalletTransaction.objects.filter(amount__lt=0).count(),"synthetic":True,"releaseReady":False}
	write(ROOT/"ready.json",result("ready","ready"),True)
	deadline=time.monotonic()+1200
	while time.monotonic()<deadline:
		requests=list((ROOT/"requests").iterdir())
		if len(requests)>64: raise ValueError()
		for request in sorted(requests):
			if request.suffix!=".request" or uuid.UUID(hex=request.stem).hex!=request.stem: raise ValueError()
			response=ROOT/"results"/(request.stem+".json")
			if response.exists(): continue
			command=private(request,128).strip()
			if command not in OPS: raise ValueError()
			outcome="simulated" if command.startswith("fake_") else "ready"
			if command in {"wallet","wallet-paused"}:
				attempt=ROOT/"attempts"/request.stem
				if attempt.exists(): outcome="uncertain_do_not_retry"
				else:
					write(attempt,{"operation":"wallet","request":request.stem})
					with transaction.atomic():
						accepted=purchase_item(saved["user"],saved["product"],1,currency="USD",expected_total=10)
						if command=="wallet-paused":
							assert accepted.ok
							write(ROOT/("held-"+request.stem+".json"),result(command,"transaction-held"))
							time.sleep(120)
							raise ValueError()
					outcome="purchased" if accepted.ok else "rejected"
			write(response,result(command,outcome))
			if command=="stop": return
		time.sleep(0.1)

if __name__=="__main__":
	try:
		if len(sys.argv)!=2: raise ValueError()
		main(sys.argv[1])
	except Exception:
		sys.stderr.write("Synthetic legacy runtime rejected; no live transport is configured.\n")
		raise SystemExit(78)
