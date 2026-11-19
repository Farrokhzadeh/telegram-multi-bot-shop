import argparse
import datetime
import importlib.util
import json
import os
import pathlib
import stat
import subprocess
import sys
import tempfile

SPEC = importlib.util.spec_from_file_location('source_copy', pathlib.Path(__file__).with_name('prepare-legacy-source-copy.py'))
SOURCE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SOURCE)
POLICY = 'manual_preservation_no_posting_conversion_or_waiver_v1'
HISTORY_POLICY = 'manual_irr_and_original_nonposting_history_v2'

def extract(instance, archive, retain_history=False):
  (brand, _, digest, size), stage, _ = SOURCE.source_snapshot(instance)
  if archive.is_symlink() or not archive.is_file() or archive.stat().st_size != size or SOURCE.ASSETS.digest(archive) != digest:
    raise ValueError()
  with archive.open('rb') as stream:
    result = subprocess.run(['docker', 'run', '--rm', '-i', '--network=none', '--read-only', '--memory=192m', '--memory-swap=192m', '--cpus=0.5', '--pids-limit=64', '--user=70:70', '--cap-drop=ALL', '--security-opt=no-new-privileges', SOURCE.IMAGE, 'pg_restore', '--data-only', '--no-owner', '--no-privileges', '--table=users', '--table=deployment_identity', *(['--table=transactions'] if retain_history else []), '--file=-'], stdin=stream, capture_output=True, timeout=50)
  if result.returncode or SOURCE.ASSETS.digest(archive) != digest:
    raise ValueError()
  retained = {'users', 'deployment_identity'} | ({'transactions'} if retain_history else set())
  tables, counts = SOURCE.ASSETS.INVENTORY.copy_tables(result.stdout.decode('utf-8'), retained)
  if set(counts) != retained:
    raise ValueError()
  identities = tables['deployment_identity']
  if len(identities) != 1 or identities[0].get('id') != '1' or identities[0].get('instance_id') != instance or identities[0].get('deployment_stage') != stage:
    raise ValueError()
  entries, wallets, seen = [], [], set()
  for row in tables['users']:
    identity = row['id']
    if not identity or not identity.isascii() or not identity.isdecimal() or int(identity) <= 0 or str(int(identity)) != identity or identity in seen:
      raise ValueError()
    seen.add(identity)
    amount = row['balance_irr']
    wallets.append({'legacy_user_id': identity, 'original_irr_amount': amount})
    if SOURCE.ASSETS.INVENTORY.number(amount) != 0:
      entries.append({'legacy_user_id': identity, 'currency': 'IRR', 'original_amount': amount})
  value = {'source': instance, 'brand': brand, 'archive_sha256': digest, 'source_users': counts['users'], 'entries': sorted(entries, key=lambda row: int(row['legacy_user_id']))}
  if retain_history:
    identities = set()
    for row in tables['transactions']:
      if not row['id'] or len(row['id']) > 256 or row['id'] in identities or row['user_id'] not in seen or row['currency'] not in ('USD', 'IRR'):
        raise ValueError()
      identities.add(row['id']); SOURCE.ASSETS.INVENTORY.number(row['amount'])
      if datetime.datetime.fromisoformat(row['created_at']).utcoffset() != datetime.timedelta(0):
        raise ValueError()
    value.update(wallets=sorted(wallets, key=lambda row: int(row['legacy_user_id'])), history=sorted(tables['transactions'], key=lambda row: row['id']))
  return value

def preserve(destination, sources, retain_history=False):
  if set(sources) != set(SOURCE.ASSETS.BRANDS) and not (set(sources) == {'staging'} and retain_history):
    raise ValueError()
  parent = destination.parent
  if parent.resolve() != parent or not parent.is_dir() or stat.S_IMODE(parent.stat().st_mode) != 0o700 or parent.stat().st_uid != os.geteuid() or destination.is_symlink():
    raise ValueError()
  rows = [extract(instance, sources[instance], True) if retain_history else extract(instance, sources[instance]) for instance in sorted(sources)]
  policy = HISTORY_POLICY if retain_history else POLICY
  content = (json.dumps({'version': 2 if retain_history else 1, 'policy': policy, 'sources': rows}, sort_keys=True, indent=2) + '\n').encode()
  if destination.exists():
    if not destination.is_file() or stat.S_IMODE(destination.stat().st_mode) != 0o600 or destination.stat().st_uid != os.geteuid() or destination.read_bytes() != content:
      raise ValueError()
  else:
    fd, temporary = tempfile.mkstemp(prefix='.irr-register-', dir=parent)
    try:
      with os.fdopen(fd, 'wb') as stream:
        stream.write(content); stream.flush(); os.fsync(stream.fileno())
      if pathlib.Path(temporary).read_bytes() != content:
        raise ValueError()
      os.link(temporary, destination, follow_symlinks=False)
      directory = os.open(parent, os.O_RDONLY | os.O_DIRECTORY)
      try:
        os.fsync(directory)
      finally:
        os.close(directory)
    finally:
      os.unlink(temporary)
  summary = [{'source': row['source'], 'brand': row['brand'], 'archive_sha256': row['archive_sha256'], 'source_users': row['source_users'], 'preserved_nonzero_irr_balances': len(row['entries'])} for row in rows]
  if retain_history:
    for result, row in zip(summary, rows, strict=True):
      result.update(preserved_history=len(row['history']), preserved_irr_history=sum(r['currency'] == 'IRR' for r in row['history']), preserved_zero_history=sum(SOURCE.ASSETS.INVENTORY.number(r['amount']) == 0 for r in row['history']))
  return {'scope': 'private_wallet_history_preservation_only' if retain_history else 'private_manual_irr_preservation_only', 'policy': policy, 'release_ready': False, 'operational_postings': 0, 'sources': summary}

def main():
  parser = argparse.ArgumentParser()
  parser.add_argument('--register', type=pathlib.Path, required=True)
  parser.add_argument('--retain-history', action='store_true')
  for instance in (*SOURCE.ASSETS.BRANDS, 'staging'):
    parser.add_argument('--' + instance, type=pathlib.Path)
  args = parser.parse_args()
  try:
    sources = {instance: getattr(args, instance) for instance in (*SOURCE.ASSETS.BRANDS, 'staging') if getattr(args, instance) is not None}
    print(json.dumps(preserve(args.register, sources, args.retain_history), sort_keys=True, indent=2))
    return 0
  except Exception:
    print('Legacy IRR preservation rejected; retain protected inputs for review.', file=sys.stderr)
    return 1

if __name__ == '__main__':
  sys.exit(main())
