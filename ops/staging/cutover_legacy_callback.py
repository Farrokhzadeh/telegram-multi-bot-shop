import hashlib,hmac,json,os,secrets,sys,time,uuid
from decimal import Decimal
from legacy import ROOT,private,write
if not __debug__: raise SystemExit(78)
def main(mode):
	assert os.geteuid()==1000 and mode in {'init','reject','pending','accept-crash','review','replay'} and os.getenv('SHOP_CUTOVER_STAGE')=='synthetic-acc-abad-v1'
	assert os.getenv('POSTGRES_HOST')=='127.0.0.1' and os.getenv('POSTGRES_DB')=='t068_legacy_stage' and os.getenv('INSTANCE_ID')=='t068-acc-abad' and os.getenv('DEPLOYMENT_STAGE')=='staging'
	run=uuid.UUID(hex=private(ROOT/'identity',64).strip()).hex;state=json.loads(private(ROOT/'state.json'));assert state['run']==run
	os.environ.setdefault('DJANGO_SETTINGS_MODULE','django_project.settings.base')
	import django
	django.setup()
	from django.db import connection,transaction
	from django.test import RequestFactory
	from apps.common.instance import assert_database_binding
	from apps.common.models import BackgroundJob
	from apps.common.services.settings import get_panel_setting,set_panel_setting
	from apps.accounts.models import TelegramUser
	from apps.payments.models import PaymentAttempt
	from apps.payments.services import create_payment_attempt,record_invoice_result
	from apps.payments.views import nowpayments_webhook
	from apps.store.models import WalletTransaction,InventoryItem,Order,Product
	assert_database_binding();assert connection.settings_dict['NAME']=='t068_legacy_stage' and TelegramUser.objects.count()==Product.objects.count()==1
	def snapshot():
		with transaction.atomic(),connection.cursor() as c:
			c.execute('SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY');c.execute("SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename");tables=[r[0] for r in c.fetchall()];rows={}
			for table in tables:
				c.execute('SELECT row_to_json(t)::text FROM '+connection.ops.quote_name(table)+' t ORDER BY 1');values=[r[0] for r in c.fetchall()];rows[table]={'count':len(values),'sha256':hashlib.sha256(json.dumps(values,separators=(',',':')).encode()).hexdigest()}
			return rows
	keypath=ROOT/'callback-key.json'
	if mode=='init':
		assert not keypath.exists() and PaymentAttempt.objects.count()==0
		from asgiref.sync import async_to_sync
		from apps.bot.database import init
		async_to_sync(init)()
		secret=secrets.token_hex(32);set_panel_setting('NP_IPN_SECRET',secret);assert get_panel_setting('BOT_TOKEN','')=='' and get_panel_setting('NP_API_KEY','')==''
		attempt=create_payment_attempt(user_id=state['user'],kind=PaymentAttempt.KIND_WALLET,amount=Decimal('10.25'),pay_currency='usdtbsc');record_invoice_result(attempt.pk,{'id':'synthetic-'+run},'https://stage.invalid/invoice')
		write(keypath,{'run':run,'secret':secret,'invoice':attempt.pk,'provider':'synthetic-'+run});write(ROOT/'callback-before.json',{'tables':snapshot(),'balance':str(TelegramUser.objects.get(pk=state['user']).balance),'ledger':WalletTransaction.objects.count(),'jobs':BackgroundJob.objects.count(),'orders':Order.objects.count(),'allocated':InventoryItem.objects.filter(order__isnull=False).count()});return
	key=json.loads(private(keypath));assert key['run']==run and get_panel_setting('NP_IPN_SECRET','')==key['secret'];before=json.loads(private(ROOT/'callback-before.json',65536));factory=RequestFactory()
	def call(status='finished',invalid=False,unbound=False,body=None,method='post'):
		v={'order_id':'unbound-'+run if unbound else key['invoice'],'payment_status':status,'payment_id':key['provider'],'invoice_id':key['provider'],'price_amount':10.25,'price_currency':'usd','pay_currency':'usdtbsc'};canonical=json.dumps(v,separators=(',',':'),sort_keys=True).encode();signature='0'*128 if invalid else hmac.new(key['secret'].encode(),canonical,hashlib.sha512).hexdigest();raw=canonical if body is None else body(canonical)
		r=nowpayments_webhook(getattr(factory,method)('/webhooks/nowpayments/',data=raw,content_type='application/json',HTTP_X_NOWPAYMENTS_SIG=signature));return r.status_code,json.loads(r.content)
	if mode=='reject':
		assert call(invalid=True)[0]==403 and call(body=lambda _:b'{"x":1,"x":2}')[0]==400 and call(body=lambda _:b'[]')[0]==400 and call(body=lambda _:b'x'*262145)[0]==413 and call(unbound=True)[0]==400
		r=nowpayments_webhook(factory.get('/webhooks/nowpayments/'));assert r.status_code==405 and snapshot()==before['tables']
	elif mode=='pending':
		assert call('confirming')[0]==200;attempt=PaymentAttempt.objects.get(pk=key['invoice']);assert attempt.provider_status=='confirming' and attempt.status==PaymentAttempt.STATUS_INVOICE_CREATED and attempt.paid_at is None
		assert {t for t,v in snapshot().items() if v!=before['tables'][t]}=={PaymentAttempt._meta.db_table};write(ROOT/'callback-pending.json',snapshot())
	elif mode=='accept-crash':
		assert call()[0]==200
		write(ROOT/'callback-commit-marker.json',{'run':run,'committed':True})
		time.sleep(60);raise SystemExit(79)
	elif mode in {'review','replay'}:
		attempt=PaymentAttempt.objects.get(pk=key['invoice']);user=TelegramUser.objects.get(pk=state['user']);assert attempt.status==PaymentAttempt.STATUS_FULFILLED and attempt.provider_payment_id==key['provider'] and attempt.notification_status==PaymentAttempt.NOTIFY_PENDING and attempt.paid_at is not None and attempt.fulfilled_at is not None
		assert user.balance==Decimal(before['balance'])+Decimal('10.25') and WalletTransaction.objects.count()==before['ledger']+1 and BackgroundJob.objects.count()==before['jobs']+1 and Order.objects.count()==before['orders'] and InventoryItem.objects.filter(order__isnull=False).count()==before['allocated']
		job=BackgroundJob.objects.filter(kind='payment_notification').get();assert job.payload['reference']==attempt.pk and job.status==BackgroundJob.STATUS_PENDING and job.attempts==0 and job.dedupe_key=='payment-notification:'+attempt.pk
		accepted=snapshot();assert set(accepted)==set(before['tables']) and {t for t,v in accepted.items() if v!=before['tables'][t]}=={'users','transactions','payment_attempts','background_jobs','user_activity'}
		for body in [None,lambda b:b'\n'+b+b'\n']:
			code,result=call(body=body);assert code==200 and result['duplicate'] is True and snapshot()==accepted
		assert call(unbound=True)[0]==400 and snapshot()==accepted
		if mode=='review': write(ROOT/'callback-accepted.json',accepted)
		else: assert accepted==json.loads(private(ROOT/'callback-accepted.json',65536))
	write(ROOT/('callback-'+mode+'-result.json'),{'run':run,'mode':mode,'synthetic':True,'release_ready':False,'tables':snapshot()})
if __name__=='__main__':
	try:
		assert len(sys.argv)==2;main(sys.argv[1])
	except Exception:
		print('Synthetic legacy callback qualification rejected.',file=sys.stderr);raise SystemExit(78)
