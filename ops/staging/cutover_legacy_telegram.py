import asyncio,hashlib,hmac,json,os,secrets,sys,time,uuid
from pathlib import Path
from legacy import ROOT,private,write
if not __debug__: raise SystemExit(78)
def failure(exc):
	frames=[];tb=exc.__traceback__
	while tb is not None: frames.append([Path(tb.tb_frame.f_code.co_filename).name,tb.tb_frame.f_code.co_name,tb.tb_lineno]);tb=tb.tb_next
	return {'error':type(exc).__name__,'frames':frames}
def sync(path):
	fd=os.open(path,os.O_RDONLY|os.O_DIRECTORY)
	try: os.fsync(fd)
	finally: os.close(fd)
def main(mode,event=None):
	assert os.geteuid()==1000 and mode in {'init','dispatch','snapshot'} and os.getenv('SHOP_CUTOVER_STAGE')=='synthetic-acc-abad-v1' and os.getenv('POSTGRES_HOST')=='127.0.0.1' and os.getenv('POSTGRES_DB')=='t068_legacy_stage' and os.getenv('INSTANCE_ID')=='t068-acc-abad' and os.getenv('DEPLOYMENT_STAGE')=='staging'
	run=uuid.UUID(hex=private(ROOT/'identity',64).strip()).hex;state=json.loads(private(ROOT/'state.json'));assert state['run']==run
	os.environ.setdefault('DJANGO_SETTINGS_MODULE','django_project.settings.base')
	import django
	django.setup()
	from django.db import connection,transaction
	from apps.common.instance import assert_database_binding
	from apps.common.services.settings import get_panel_setting,set_panel_setting
	from apps.accounts.models import TelegramUser
	from apps.store.models import Product
	assert_database_binding();assert connection.settings_dict['NAME']=='t068_legacy_stage' and TelegramUser.objects.count()==1 and Product.objects.count()==(2 if (ROOT/'stock-plan.json').exists() else 1)
	def snapshot():
		with transaction.atomic(),connection.cursor() as c:
			c.execute('SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY');c.execute("SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename");tables=[r[0] for r in c.fetchall()];rows={}
			for table in tables:
				c.execute('SELECT row_to_json(t)::text FROM '+connection.ops.quote_name(table)+' t ORDER BY 1');values=[r[0] for r in c.fetchall()];rows[table]={'count':len(values),'sha256':hashlib.sha256(json.dumps(values,separators=(',',':')).encode()).hexdigest()}
			return rows
	if mode=='init':
		assert event is None and not (ROOT/'telegram-key.json').exists()
		from asgiref.sync import async_to_sync
		from apps.bot import database as db
		async_to_sync(db.init)();set_panel_setting('BOT_STATUS','active');assert get_panel_setting('BOT_TOKEN','')=='' and get_panel_setting('NP_API_KEY','')==''
		async_to_sync(db.set_user_language)(state['user'],'en');async_to_sync(db.set_state)(state['user'],None)
		write(ROOT/'telegram-key.json',{'run':run,'actor':state['user'],'bot':734000001,'username':'synthetic_accabad_stage_bot','anchor':101,'date':int(time.time()),'secret':secrets.token_hex(32),'token':secrets.token_hex(32)});write(ROOT/'telegram-before.json',snapshot());sync(ROOT);return
	k=json.loads(private(ROOT/'telegram-key.json'));assert k['run']==run and k['actor']==state['user'] and get_panel_setting('BOT_TOKEN','')==''
	if mode=='snapshot':
		assert event in {'after','restart'};write(ROOT/('telegram-'+event+'.json'),snapshot());sync(ROOT);return
	assert isinstance(event,str) and uuid.UUID(hex=event).hex==event;raw=private(ROOT/'telegram-inputs'/(event+'.json'),8192);v=json.loads(raw);assert set(v)=={'run','event','body','header'} and v['run']==run and v['event']==event and hmac.compare_digest(v['header'],k['secret'])
	from apps.bot import handlers
	from telegram import Update
	from telegram.request import BaseRequest
	from telegram.ext import Application,CallbackQueryHandler,CommandHandler,filters
	class LocalRequest(BaseRequest):
		@property
		def read_timeout(self): return 5
		async def initialize(self): pass
		async def shutdown(self): pass
		async def do_request(self,url,method,request_data=None,**kwargs):
			api=url.rsplit('/',1)[-1];data=request_data.parameters if request_data else {};calls.append({'api':api,'parameters':data});assert method=='POST' and api in {'getMe','sendMessage','sendRichMessage','editMessageText','answerCallbackQuery'}
			bot={'id':k['bot'],'is_bot':True,'first_name':'SYNTHETIC BOT','username':k['username']}
			if api=='getMe': result=bot
			elif api=='answerCallbackQuery': assert data['callback_query_id']==update.callback_query.id;result=True
			else:
				assert int(data['chat_id'])==k['actor']
				if api=='editMessageText': assert int(data['message_id'])==k['anchor']
				result={'message_id':int(data.get('message_id',k['anchor']+1)),'date':k['date'],'chat':{'id':k['actor'],'type':'private'},'from':bot,'text':data['text'] if 'text' in data else data['rich_message']['html']}
			return 200,json.dumps({'ok':True,'result':result}).encode()
	calls=[];errors=[];update=None
	async def execute():
		nonlocal update
		app=Application.builder().token(str(k['bot'])+':'+k['token']).request(LocalRequest()).get_updates_request(LocalRequest()).updater(None).build()
		app.add_handler(CommandHandler(['menu'],handlers.handle_commands,filters=filters.UpdateType.MESSAGE));app.add_handler(CallbackQueryHandler(handlers.button_handler))
		async def failed(update,context): errors.append(failure(context.error))
		app.add_error_handler(failed)
		await app.initialize()
		try:
			update=Update.de_json(json.loads(v['body']),app.bot);assert update.effective_user.id==k['actor'] and not update.effective_user.is_bot and update.effective_chat.id==k['actor'] and update.effective_chat.type=='private'
			if update.callback_query: assert update.callback_query.message.from_user.id==k['bot'] and update.callback_query.message.message_id==k['anchor'] and update.callback_query.data in {'profile','admin_panel'}
			else: assert update.message.text=='/menu'
			await app.process_update(update)
		finally: await app.shutdown()
	asyncio.run(execute());write(ROOT/('telegram-transcript-'+event+'.json'),{'run':run,'event':event,'calls':calls,'errors':errors});sync(ROOT);assert not errors and calls[0]['api']=='getMe';apis=[x['api'] for x in calls]
	assert apis==(['getMe','sendRichMessage'] if update.message else ['getMe','answerCallbackQuery'] if update.callback_query.data=='admin_panel' else ['getMe','answerCallbackQuery','editMessageText'])
	pause=ROOT/'telegram-pause-event.json'
	if pause.exists() and json.loads(private(pause))=={'run':run,'event':event}: write(ROOT/('telegram-committed-'+event+'.json'),{'run':run,'event':event});sync(ROOT);time.sleep(60);raise SystemExit(79)
	write(ROOT/'telegram-results'/(event+'.json'),{'run':run,'event':event,'input_sha256':hashlib.sha256(raw.encode()).hexdigest(),'status':200,'synthetic':True,'release_ready':False,'transport_sha256':hashlib.sha256(private(ROOT/('telegram-transcript-'+event+'.json'),65536).encode()).hexdigest()});sync(ROOT/'telegram-results')
if __name__=='__main__':
	try:
		assert len(sys.argv) in {2,3};main(sys.argv[1],sys.argv[2] if len(sys.argv)==3 else None)
	except Exception as exc:
		print(json.dumps(failure(exc)),file=sys.stderr);raise SystemExit(78)
