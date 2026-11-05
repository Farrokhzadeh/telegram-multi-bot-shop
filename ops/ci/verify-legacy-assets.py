import argparse
import base64
import collections
import contextlib
import errno
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import shutil
import stat
import subprocess
import sys
import tempfile

SPEC = importlib.util.spec_from_file_location('legacy_inventory', pathlib.Path(__file__).with_name('inspect-legacy-backups.py'))
INVENTORY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INVENTORY)
BRANDS = {'prod1': ('acc-abad', 'iran', '0e86433927fa420ad3e13bbff2098119d47dfbaa0b626573c552999902703667', 7419590), 'prod2': ('kuala', 'outside_iran', 'a61aaddbbd76a10aa6894aa02cffe861cb44a9602b1d085eecc981f822656071', 259270), 'prod3': ('zikotech', 'iran', '4481980586074034e84ff11d867621a1aa847c18589576ab7dd89155a21d6245', 1642702)}
FIELDS = {'inventory': ('data', 'enctext:v1:'), 'service_jobs': ('submitted_data', 'encjson:v1:'), 'supplier_connections': ('credential', 'enctext:v1:'), 'supplier_fulfillment_items': ('content', 'enctext:v1:'), 'supplier_refund_records': ('external_settlement_reference', 'enctext:v1:'), 'preorder_refund_records': ('external_settlement_reference', 'enctext:v1:')}
ENV_NAMES = frozenset(('FIELD_ENCRYPTION_KEY', 'FIELD_ENCRYPTION_KEY_PREVIOUS', 'DJANGO_SECRET_KEY', 'INSTANCE_ID', 'DEPLOYMENT_STAGE', 'INSTANCE_ENV_FILE', 'INSTANCE_DATA_DIR'))

def digest(path):
  with path.open('rb') as stream:
    return hashlib.file_digest(stream, 'sha256').hexdigest()

def env_values(path):
  from dotenv.parser import parse_stream
  if path.stat().st_size > 1048576:
    raise ValueError()
  values = {}
  with path.open(encoding='utf-8-sig') as stream:
    for binding in parse_stream(stream):
      if binding.error:
        raise ValueError()
      if binding.key in ENV_NAMES:
        if binding.key in values:
          raise ValueError()
        values[binding.key] = binding.value or ''
  if any('${' in value or '\x00' in value for value in values.values()):
    raise ValueError()
  return values

def local_path(root, value):
  path = pathlib.Path(value)
  return (root / path).resolve() if not path.is_absolute() else path.resolve()

def configuration(root, instance):
  path = root / 'instances' / instance / '.env'
  config = env_values(path)
  if config.get('INSTANCE_ID') != instance or config.get('DEPLOYMENT_STAGE') != 'production' or not config.get('INSTANCE_DATA_DIR'):
    raise ValueError()
  app_path = local_path(root, config['INSTANCE_ENV_FILE']) if config.get('INSTANCE_ENV_FILE') else path
  app = env_values(app_path) if app_path.resolve() != path.resolve() else config
  if app.get('INSTANCE_ID', instance) != instance:
    raise ValueError()
  media = local_path(root, config['INSTANCE_DATA_DIR']) / 'media'
  return app, media

def keyring(values):
  from cryptography.fernet import Fernet
  result, seen = [], set()
  for slot, raw in (('active', values.get('FIELD_ENCRYPTION_KEY') or values.get('DJANGO_SECRET_KEY')), ('previous', values.get('FIELD_ENCRYPTION_KEY_PREVIOUS'))):
    if not raw:
      continue
    candidate = raw[7:] if raw.startswith('fernet:') else raw
    key = None
    if len(candidate) == 44:
      try:
        decoded = base64.urlsafe_b64decode(candidate.encode('ascii'))
        if len(decoded) == 32:
          key = candidate.encode('ascii')
      except (ValueError, UnicodeError):
        pass
    key = key or base64.urlsafe_b64encode(hashlib.sha256(candidate.encode('utf-8')).digest())
    if key not in seen:
      result.append((slot, Fernet(key)))
      seen.add(key)
  return result

def authenticate(value, prefix, keys):
  from cryptography.fernet import InvalidToken
  if value in (None, ''):
    return 'empty'
  if not value.startswith(prefix):
    return 'unsupported_representation'
  if not keys:
    return 'missing_key'
  try:
    token = value[len(prefix):].encode('ascii')
  except UnicodeError:
    return 'invalid_ciphertext'
  for slot, cipher in keys:
    plain = None
    try:
      plain = cipher.decrypt(token)
      text = plain.decode('utf-8')
      if prefix == 'encjson:v1:':
        json.loads(text)
      return 'authenticated_' + slot
    except InvalidToken:
      continue
    except (UnicodeError, ValueError):
      return 'invalid_plaintext_encoding'
    finally:
      plain = None
  return 'authentication_failed'

def relative_media(value):
  if not isinstance(value, str) or not value or '\x00' in value or '\\' in value:
    raise ValueError()
  for prefix in ('/app/data/media/', 'data/media/'):
    if value.startswith(prefix):
      value = value[len(prefix):]
      break
  parts = value.split('/')
  if any(part in ('', '.', '..') for part in parts) or pathlib.PurePosixPath(value).is_absolute():
    raise ValueError()
  return parts

def check_media(root, reference, expected_hash=None, expected_size=None):
  try:
    parts = relative_media(reference)
    if expected_hash and not re.fullmatch('[a-fA-F0-9]{64}', expected_hash):
      return 'invalid_metadata'
    if expected_size is not None and (not str(expected_size).isdigit() or int(expected_size) < 0):
      return 'invalid_metadata'
    with contextlib.ExitStack() as stack:
      parent = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
      stack.callback(os.close, parent)
      for part in parts[:-1]:
        parent = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
        stack.callback(os.close, parent)
      fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=parent)
      stack.callback(os.close, fd)
      before = os.fstat(fd)
      if not stat.S_ISREG(before.st_mode):
        return 'not_regular'
      if expected_size is not None and before.st_size != int(expected_size):
        return 'size_mismatch'
      hasher = hashlib.sha256()
      while chunk := os.read(fd, 1048576):
        hasher.update(chunk)
      after = os.fstat(fd)
      linked = os.stat(parts[-1], dir_fd=parent, follow_symlinks=False)
      if (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns) or (after.st_dev, after.st_ino) != (linked.st_dev, linked.st_ino):
        return 'changed_during_read'
      if expected_hash and hasher.hexdigest() != expected_hash.lower():
        return 'hash_mismatch'
      return 'verified_hash' if expected_hash else 'present_without_hash'
  except ValueError:
    return 'invalid_reference'
  except OSError as error:
    if error.errno == errno.ENOENT:
      return 'missing'
    return 'unsafe_path' if error.errno in (errno.ELOOP, errno.ENOTDIR) else 'unreadable'

def archived_tables(path, expected, executable, docker_root=None):
  if digest(path) != expected:
    raise ValueError()
  names = sorted(set(FIELDS) | {'manual_deposits', 'products'})
  args = [executable, '--data-only', '--file=-', '--no-owner', '--no-privileges', '--schema=public']
  args.extend('--table=' + table for table in names)
  if docker_root is None:
    run = subprocess.run(args + [str(path)], capture_output=True, check=False)
  else:
    command = ['docker', 'compose', '--env-file', str(docker_root / 'fleet.env'), '-f', str(docker_root / 'docker-compose.fleet-infra.yml'), 'exec', '-T', 'postgres'] + args
    with path.open('rb') as source:
      run = subprocess.run(command, stdin=source, capture_output=True, check=False)
  if run.returncode or digest(path) != expected:
    raise ValueError()
  tables, counts = INVENTORY.copy_tables(run.stdout.decode('utf-8'))
  if set(counts) != set(names):
    raise ValueError()
  return tables

def verify(tables, values, media):
  keys, crypto, files, exceptions = keyring(values), {}, collections.Counter(), []
  def exception(table, row, field, status, reference=None):
    detail = {'table': table, 'source_id': row['id'], 'field': field, 'status': status}
    if reference is not None:
      detail['reference'] = reference
    exceptions.append(detail)
  for table, (field, prefix) in FIELDS.items():
    counts = collections.Counter()
    for row in tables[table]:
      result = authenticate(row[field], prefix, keys)
      counts[result] += 1
      if result not in ('empty', 'authenticated_active', 'authenticated_previous'):
        exception(table, row, field, result)
    crypto[table] = dict(counts)
  for row in tables['manual_deposits']:
    reference = row['proof_file_path']
    if not reference:
      files['no_local_reference'] += 1
      continue
    result = check_media(media, reference, row['proof_sha256'], row['proof_size_bytes'])
    files[result] += 1
    if result != 'verified_hash':
      exception('manual_deposits', row, 'proof_file_path', result, reference)
  authenticated = sum(v.get('authenticated_active', 0) + v.get('authenticated_previous', 0) for v in crypto.values())
  checked = sum(sum(v.values()) - v.get('empty', 0) for v in crypto.values())
  failures = sum(sum(v.values()) - v.get('empty', 0) - v.get('authenticated_active', 0) - v.get('authenticated_previous', 0) for v in crypto.values())
  remote = 0
  for row in tables['products']:
    if row['post_purchase_media_id']:
      remote += 1
      exception('products', row, 'post_purchase_media_id', 'remote_reference_not_verified', row['post_purchase_media_id'])
  result = {'encrypted_fields': crypto, 'source_authentication': 'not_exercised' if not checked else 'passed' if not failures else 'needs_review', 'authenticated_records': authenticated, 'receipt_files': dict(files), 'product_media_references_unverified': remote, 'exception_records': len(exceptions), 'target_reencryption_qualified': False, 'release_ready': False}
  return result, exceptions

def find_archives(roots, instances):
  found, candidates, seen = {}, [], set()
  for root in roots:
    if root.is_file():
      candidates.append(root)
    elif root.is_dir():
      for directory, dirs, files in os.walk(root, followlinks=False):
        dirs[:] = sorted(d for d in dirs if not (pathlib.Path(directory) / d).is_symlink())
        candidates.extend(pathlib.Path(directory) / f for f in sorted(files) if f.endswith('.dump'))
        if len(candidates) > 10000:
          raise ValueError()
  for path in candidates:
    resolved = path.resolve()
    if resolved in seen:
      continue
    seen.add(resolved)
    try:
      size = resolved.stat().st_size
      matches = [i for i in instances if i not in found and BRANDS[i][3] == size]
      if matches:
        actual = digest(resolved)
        for instance in matches:
          if actual == BRANDS[instance][2]:
            found[instance] = resolved
    except OSError:
      continue
  return found

def write_report(directory, filename, body):
  fd = os.open(directory / filename, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
  with os.fdopen(fd, 'w', encoding='utf-8') as stream:
    json.dump(body, stream, sort_keys=True, indent=2)
    stream.write('\n')

def main():
  parser = argparse.ArgumentParser(description='Read-only local verification of pinned AccAbad backups, existing fleet key files and referenced media; no live database or network calls.')
  parser.add_argument('--fleet-root', type=pathlib.Path, default=pathlib.Path.cwd())
  parser.add_argument('--backup-root', type=pathlib.Path, action='append')
  parser.add_argument('--instance', choices=tuple(BRANDS), action='append')
  parser.add_argument('--pg-restore', default='pg_restore')
  parser.add_argument('--decoder', choices=('auto', 'native', 'docker'), default='auto')
  parser.add_argument('--output-parent', type=pathlib.Path, default=pathlib.Path.home() / '.local' / 'state' / 'teleg-verification')
  args = parser.parse_args()
  try:
    from cryptography.fernet import Fernet
    from dotenv.parser import parse_stream
    root, output = args.fleet_root.resolve(), args.output_parent.resolve()
    if output.is_relative_to(root) or output.is_relative_to(pathlib.Path(__file__).resolve().parents[2]):
      raise ValueError()
    instances = list(dict.fromkeys(args.instance or [i for i in BRANDS if (root / 'instances' / i / '.env').is_file()]))
    if not instances:
      raise ValueError()
    roots = args.backup_root or [root / '.release' / 'ci' / 'backups', root / 'data' / 'backups']
    archives = find_archives(roots, instances)
    docker = args.decoder == 'docker' or (args.decoder == 'auto' and not shutil.which(args.pg_restore))
    if docker and (not shutil.which('docker') or not (root / 'fleet.env').is_file() or not (root / 'docker-compose.fleet-infra.yml').is_file()):
      raise ValueError()
    summaries, details = {}, {}
    for instance in instances:
      brand, market, sha, _ = BRANDS[instance]
      try:
        stage = 'configuration_unavailable'
        values, media = configuration(root, instance)
        stage = 'pinned_archive_not_found'
        archive = archives[instance]
        stage = 'pinned_archive_or_decoder_rejected'
        tables = archived_tables(archive, sha, 'pg_restore' if docker else args.pg_restore, root if docker else None)
        stage = 'record_inspection_rejected'
        result, exceptions = verify(tables, values, media)
        result.update(brand=brand, market=market, archive_sha256=sha, inspection_complete=True)
        summaries[instance], details[instance] = result, exceptions
      except (OSError, ValueError, KeyError, TypeError, OverflowError):
        summaries[instance] = {'brand': brand, 'inspection_complete': False, 'status': stage, 'release_ready': False}
    output.mkdir(mode=0o700, parents=True, exist_ok=True)
    directory = pathlib.Path(tempfile.mkdtemp(prefix='legacy-assets-', dir=output))
    report = {'scope': 'selected_source_ciphertext_and_local_receipts_only', 'source_revision': '66371a23a1c8149dc5bdf89a0355aa689d454dba', 'decoder': 'existing_fleet_container' if docker else 'native', 'release_ready': False, 'instances': summaries, 'not_inspected': sorted(set(BRANDS) - set(instances))}
    write_report(directory, 'summary.json', report)
    write_report(directory, 'exceptions.private.json', details)
    print('Verification finished. Share only summary.json; keep exceptions.private.json local.')
    print(str(directory))
    return 0 if all(v.get('inspection_complete') and v['source_authentication'] != 'needs_review' and not v['exception_records'] and not v['product_media_references_unverified'] for v in summaries.values()) else 1
  except ImportError:
    print('Verification dependencies unavailable; use the existing legacy Python environment or install the pinned verifier requirements into a separate virtual environment.', file=sys.stderr)
  except (OSError, ValueError, TypeError, KeyError):
    print('Verification setup rejected. Check the fleet directory, selected instances, backup locations and output directory; no secret values are required as arguments.', file=sys.stderr)
  return 2

if __name__ == '__main__':
  sys.exit(main())
