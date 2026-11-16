import asyncio
import os
import resource
import time
from types import SimpleNamespace
os.environ.setdefault('DJANGO_SETTINGS_MODULE','django_project.settings.test')
import django
from django.conf import settings
settings.DATABASES['default']['NAME']='/tmp/t076-legacy.sqlite3'
django.setup()
from django.core.management import call_command
call_command('migrate',verbosity=0,interactive=False,run_syncdb=True)
from apps.accounts.models import TelegramUser
from apps.store.models import Product
TelegramUser.objects.create(id=7001,first_name='Synthetic',lang='en')
p=Product.objects.create(name='Synthetic listing',price='10.00',stock=0,auto_price=False)
from apps.bot.handlers import button_handler
class Query:
	answers=0
	edits=0
	data='buy_'+str(p.id)
	from_user=SimpleNamespace(id=7001,first_name='Synthetic',username=None,is_bot=False)
	message=SimpleNamespace(chat_id=7001,message_id=77,chat=SimpleNamespace(id=7001,type='private'),photo=None,video=None,document=None)
	async def answer(self,*args,**kwargs): self.answers+=1;return None
	async def edit_message_text(self,*args,**kwargs):
		text=kwargs.get('text') or (args[0] if args else '')
		if 'Synthetic listing' not in text: raise ValueError('Unexpected rendered product')
		self.edits+=1;return None
q=Query()
u=SimpleNamespace(callback_query=q,effective_user=q.from_user,effective_chat=q.message.chat,message=None)
c=SimpleNamespace(user_data={},bot=SimpleNamespace())
async def run():
	start=time.monotonic();before=resource.getrusage(resource.RUSAGE_SELF)
	for _ in range(10): await button_handler(u,c)
	after=resource.getrusage(resource.RUSAGE_SELF)
	assert q.answers==10 and q.edits==10
	print('callbacks=10 seconds=%.3f cpu_seconds=%.3f maxrss_kib=%d answers=%d edits=%d'%(time.monotonic()-start,after.ru_utime+after.ru_stime-before.ru_utime-before.ru_stime,after.ru_maxrss,q.answers,q.edits),flush=True)
asyncio.run(run())

from apps.store.models import UserActivity
print('synthetic_activity_rows=%d'%UserActivity.objects.count(),flush=True)
assert UserActivity.objects.count()==10
