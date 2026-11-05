import argparse
import collections
import importlib.util
import json
import pathlib
import sys

SPEC = importlib.util.spec_from_file_location('inventory', pathlib.Path(__file__).with_name('inspect-legacy-backups.py'))
INVENTORY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INVENTORY)

def analyze(tables):
  links = collections.defaultdict(list)
  for table in ('manual_deposits', 'payment_attempts'):
    for row in tables[table]:
      if row['fulfilled_order_id']:
        snapshot = json.loads(row['checkout_snapshot'])
        if not isinstance(snapshot, dict):
          raise ValueError()
        links[row['fulfilled_order_id']].append((bool(snapshot), row['user_id']))
  transactions = {row['id']: row for row in tables['transactions']}
  if len(transactions) != len(tables['transactions']):
    raise ValueError()
  assigned = collections.Counter(row['order_id'] for row in tables['inventory'] if row['order_id'])
  counts = collections.Counter()
  for row in tables['orders']:
    snapshot = json.loads(row['checkout_snapshot'])
    if not isinstance(snapshot, dict):
      raise ValueError()
    if snapshot:
      continue
    counts['empty'] += 1
    linked = links[row['id']]
    same = [item for item in linked if item[1] == row['user_id']]
    counts['linked_payment'] += bool(linked)
    counts['linked_same_user'] += bool(same)
    counts['linked_different_user'] += any(item[1] != row['user_id'] for item in linked)
    counts['linked_snapshot_same_user'] += any(item[0] for item in same)
    transaction = transactions.get(row['id'])
    counts['transaction_same_id'] += bool(transaction)
    if transaction:
      counts['transaction_exact'] += transaction['user_id'] == row['user_id'] and transaction['currency'] == row['currency'] and INVENTORY.number(transaction['amount']) == -INVENTORY.number(row['total'])
    quantity, count = int(row['qty']), assigned[row['id']]
    if quantity <= 0:
      raise ValueError()
    counts['inventory_none'] += count == 0
    counts['inventory_partial'] += 0 < count < quantity
    counts['inventory_complete'] += count == quantity
    counts['inventory_exceeds'] += count > quantity
  return {k: counts[k] for k in ('empty','linked_payment','linked_same_user','linked_different_user','linked_snapshot_same_user','transaction_same_id','transaction_exact','inventory_none','inventory_partial','inventory_complete','inventory_exceeds')}

def main():
  parser = argparse.ArgumentParser()
  parser.add_argument('--pg-restore', default='pg_restore')
  parser.add_argument('archives', type=pathlib.Path, nargs='+')
  args = parser.parse_args()
  try:
    sources = []
    for path in args.archives:
      meta, tables = INVENTORY.inspect(path, args.pg_restore)
      sources.append({'archive_sha256':meta['archive_sha256'], **analyze(tables)})
    print(json.dumps({'scope':'empty_order_snapshot_source_evidence_only','release_ready':False,'sources':sources}, sort_keys=True, indent=2))
  except (OSError, ValueError, KeyError, TypeError, OverflowError, INVENTORY.decimal.InvalidOperation):
    print('Order evidence inspection rejected; retain protected inputs for review.', file=sys.stderr)
    return 1
  return 0

if __name__ == '__main__':
  sys.exit(main())
