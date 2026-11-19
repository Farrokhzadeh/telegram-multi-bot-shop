import argparse
import hashlib
import hmac
import importlib.util
import json
import os
import pathlib
import re
import struct
import subprocess
import sys

SPEC = importlib.util.spec_from_file_location('assets', pathlib.Path(__file__).with_name('verify-legacy-assets.py'))
ASSETS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ASSETS)
REVISION = '66371a23a1c8149dc5bdf89a0355aa689d454dba'
STAGING_REVISION = '5752ec9c21e49b93e1d7d9f0b95bb8a7d713737d'
STAGING_SOURCE = ('acc-abad', 'iran', 'b861a77aac8fd9ddcd32cca1624cbb13844fbe5933109489ff31c237ee5ddf53', 1456403)

def source_snapshot(instance):
  if instance == 'staging':
    return STAGING_SOURCE, 'staging', STAGING_REVISION
  return ASSETS.BRANDS[instance], 'production', REVISION
IMAGE = 'sha256:e013e867e712fec275706a6c51c966f0bb0c93cfa8f51000f85a15f9865a28cb'
SCHEMA = ["SELECT table_name,column_name,ordinal_position::text,data_type,udt_name,is_nullable,COALESCE(character_maximum_length::text,''),COALESCE(numeric_precision::text,''),COALESCE(numeric_scale::text,''),COALESCE(column_default,'') FROM information_schema.columns WHERE table_schema='public' ORDER BY table_name,column_name", "SELECT c.relname,k.conname,pg_get_constraintdef(k.oid,true) FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' ORDER BY c.relname,k.conname", "SELECT tablename,indexname,indexdef FROM pg_indexes WHERE schemaname='public' ORDER BY tablename,indexname", "SELECT app,name FROM public.django_migrations ORDER BY app,name"]

def run(args, **kwargs):
  result = subprocess.run(args, capture_output=True, **kwargs)
  if result.returncode:
    raise ValueError()
  return result.stdout

def private(path, text):
  fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
  with os.fdopen(fd, 'w') as stream:
    stream.write(text)

def rows(container, database, query, width=None):
  aliases = "(" + ",".join("c" + str(i) for i in range(width)) + ")" if width else ""
  sql = "BEGIN READ ONLY; SET LOCAL TimeZone='UTC'; SELECT row_to_json(q)::text FROM (" + query + ") q" + aliases + "; COMMIT;"
  output = run(['docker','exec',container,'psql','-XqAt','-v','ON_ERROR_STOP=1','-h','/q','-U','teleg_source_owner','-d',database,'-c',sql], text=True)
  return [list(json.loads(line).values()) for line in output.splitlines()]

def binary_text(value):
  data = value.encode(); size = len(data); prefix = bytearray()
  while size >= 128:
    prefix.append((size & 127) | 128); size >>= 7
  return bytes(prefix + bytes([size])) + data

def schema_digest(groups):
  body = bytearray(binary_text('accabad-source-schema-v2'))
  for group in groups:
    if len(group) > 1000:
      raise ValueError()
    body.extend(struct.pack('<i', len(group)))
    for row in group:
      for value in row:
        body.extend(binary_text(value))
  return hashlib.sha256(body).hexdigest()

def append(hash, value):
  data = value.encode(); hash.update(struct.pack('<i', len(data))); hash.update(data)

def table_commitment(container, database, table, key, secret):
  if not all(re.fullmatch('[a-z][a-z0-9_]{0,62}', name) for name in (table, key)):
    raise ValueError()
  hash = hmac.new(secret, digestmod=hashlib.sha256)
  for value in ('accabad-source-table-v1', table, key):
    append(hash, value)
  query = f"BEGIN READ ONLY; SET LOCAL TimeZone='UTC'; SELECT json_build_array(s.{key}::text,to_jsonb(s)::text)::text FROM public.{table} s ORDER BY s.{key}::text COLLATE \"C\"; COMMIT;"
  process = subprocess.Popen(['docker','exec',container,'psql','-XqAt','-v','ON_ERROR_STOP=1','-h','/q','-U','teleg_source_owner','-d',database,'-c',query], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
  count = 0
  try:
    for line in process.stdout:
      identity, body = json.loads(line)
      append(hash, identity); append(hash, body); count += 1
      if count > 1000000:
        raise ValueError()
    if process.wait() != 0:
      raise ValueError()
  finally:
    if process.poll() is None:
      process.kill(); process.wait()
  return {'table':table,'key':key,'count':count,'commitment':hash.hexdigest()}

def prepare(root, container, instance, archive):
  (_, _, digest, size), stage, revision = source_snapshot(instance)
  if stage == 'staging' and (archive.is_symlink() or not archive.is_file()):
    raise ValueError()
  if archive.stat().st_size != size or ASSETS.digest(archive) != digest:
    raise ValueError()
  info = json.loads(run(['docker','inspect',container], text=True))[0]
  mounts = {item['Destination']:item['Source'] for item in info['Mounts'] if item['Type'] == 'bind'}
  host = info['HostConfig']
  if info['Config']['Labels'].get('teleg.purpose') != 'source-copy-inspection' or info['Config']['User'] != '70:70' or host['NetworkMode'] != 'none' or info['Image'] != IMAGE or host['PortBindings'] or mounts != {'/q':str(root/'socket'),'/var/lib/postgresql/data':str(root/'data')} or host['Memory'] != 201326592 or host['MemorySwap'] != 201326592 or host['NanoCpus'] != 500000000 or host['PidsLimit'] != 64 or not host['ReadonlyRootfs'] or host['Privileged'] or set(host['CapDrop'] or []) != {'ALL'} or 'no-new-privileges' not in (host['SecurityOpt'] or []):
    raise ValueError()
  database = 'teleg_source_' + instance
  run(['docker','exec',container,'dropdb','--if-exists','-h','/q','-U','teleg_source_owner',database])
  run(['docker','exec',container,'createdb','-h','/q','-U','teleg_source_owner',database])
  with archive.open('rb') as stream:
    run(['docker','exec','-i',container,'pg_restore','--exit-on-error','--single-transaction','--no-owner','--no-privileges','-h','/q','-U','teleg_source_owner','-d',database], stdin=stream)
  if ASSETS.digest(archive) != digest or rows(container,database,"SELECT id::text,instance_id,deployment_stage FROM public.deployment_identity ORDER BY id") != [['1',instance,stage]]:
    raise ValueError()
  setup = f"DO $x$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='teleg_source_reader') THEN CREATE ROLE teleg_source_reader LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS; END IF; END $x$; REVOKE ALL ON DATABASE {database} FROM PUBLIC; GRANT CONNECT ON DATABASE {database} TO teleg_source_reader; REVOKE ALL ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA public TO teleg_source_reader; GRANT SELECT ON ALL TABLES IN SCHEMA public TO teleg_source_reader; COMMENT ON DATABASE {database} IS 'teleg-offline-copy:{digest}'; ALTER DATABASE {database} SET default_transaction_read_only=on;"
  run(['docker','exec',container,'psql','-Xq','-v','ON_ERROR_STOP=1','-h','/q','-U','teleg_source_owner','-d',database,'-c',setup])
  groups = [rows(container, database, query, width) for query, width in zip(SCHEMA, (10,3,3,2), strict=True)]
  tables = rows(container, database, "SELECT c.relname,a.attname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_index i ON i.indrelid=c.oid AND i.indisprimary AND i.indnkeyatts=1 JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=i.indkey[0] WHERE n.nspname='public' AND c.relkind='r' ORDER BY c.relname")
  secret = os.urandom(32)
  plan = {'socket':str(root/'socket'),'database':database,'archive_sha256':digest,'source_revision':revision,'server_version':int(rows(container,database,"SELECT current_setting('server_version_num') AS version")[0][0]),'schema_sha256':schema_digest(groups),'commitment_key':secret.hex(),'tables':[table_commitment(container,database,table,key,secret) for table,key in tables]}
  private(root/(instance+'.plan.json'), json.dumps(plan,separators=(',',':')))
  private(root/(instance+'.connection'), f"Host={root/'socket'};Port=5432;Database={database};Username=teleg_source_reader;Enlist=false;Pooling=false;Include Error Detail=false")
  print(json.dumps({'instance':instance,'archive_unchanged':True,'native_restore':True,'schema_sha256':plan['schema_sha256'],'tables':len(tables),'columns':len(groups[0]),'constraints':len(groups[1]),'indexes':len(groups[2]),'migrations':len(groups[3])},sort_keys=True))

def main():
  parser = argparse.ArgumentParser()
  parser.add_argument('--root',type=pathlib.Path,required=True)
  parser.add_argument('--container',required=True)
  parser.add_argument('--instance',choices=(*ASSETS.BRANDS, 'staging'),required=True)
  parser.add_argument('--archive',type=pathlib.Path,required=True)
  args = parser.parse_args()
  try:
    root = args.root.resolve()
    if root != args.root or not root.name.startswith('teleg-source-qualification-') or root.stat().st_mode & 0o077 or not args.container.startswith('teleg-source-copy-'):
      raise ValueError()
    prepare(root,args.container,args.instance,args.archive)
  except (ValueError,KeyError,TypeError,OSError,subprocess.SubprocessError):
    print('Source copy preparation rejected; no private source details exported.',file=sys.stderr)
    return 1
  return 0

if __name__ == '__main__':
  sys.exit(main())
