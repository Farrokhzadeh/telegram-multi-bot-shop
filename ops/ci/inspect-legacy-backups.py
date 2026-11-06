import argparse
import collections
import decimal
import hashlib
import itertools
import json
import pathlib
import re
import subprocess
import sys

TABLES = frozenset('users orders products inventory transactions discounts payment_attempts manual_deposits service_jobs django_migrations common_orderdelivery common_orderdeliverypart common_deliveryattempt common_preorderdeliverybatch common_preorderdeliverybatchpart common_preorderdeliverybatch_items preorder_order_states preorder_refund_records supplier_fulfillments supplier_fulfillment_items supplier_procurement_attempts supplier_refund_records supplier_connections supplier_product_mappings supplier_availability_snapshots user_activity payments_walletfundingauthorization'.split())

def decode(value):
  if value == r'\N':
    return None
  def escaped(match):
    token = match[1]
    if token[0] in '01234567':
      return chr(int(token, 8))
    if token[0] == 'x':
      return chr(int(token[1:], 16))
    return {'t': '\t', 'n': '\n', 'r': '\r', 'b': '\b', 'f': '\f', 'v': '\v'}.get(token, token)
  if value.endswith('\\') and len(re.search(r'\\+$', value)[0]) % 2:
    raise ValueError()
  return re.sub(r'\\([0-7]{1,3}|x[0-9a-fA-F]{1,2}|.)', escaped, value)

def copy_tables(sql, retained=TABLES):
  tables, counts, current = {}, {}, None
  for line in sql.split('\n'):
    if current is not None:
      if line == r'\.':
        current = None
        continue
      values = line.split('\t')
      if len(values) != len(columns):
        raise ValueError()
      counts[current] += 1
      if current in retained:
        tables[current].append(dict(zip(columns, map(decode, values), strict=True)))
      continue
    match = re.fullmatch(r'COPY public\.([a-z_]+) \((.+)\) FROM stdin;', line)
    if match:
      current = match[1]
      columns = [c.strip('"') for c in match[2].split(', ')]
      if current in counts or len(set(columns)) != len(columns) or any(not re.fullmatch('[a-z_][a-z_0-9]*', c) for c in columns):
        raise ValueError()
      counts[current] = 0
      tables[current] = []
    elif line.startswith('COPY '):
      raise ValueError()
  if current is not None or not counts:
    raise ValueError()
  return tables, counts

def number(value):
  result = decimal.Decimal(value)
  if not result.is_finite():
    raise ValueError()
  return result

def inventory(tables, counts):
  rows = lambda name: tables.get(name, [])
  tally = lambda name, predicate: sum(bool(predicate(r)) for r in rows(name))
  snapshots = [json.loads(r['checkout_snapshot']) for r in rows('orders')]
  if any(not isinstance(s, dict) for s in snapshots):
    raise ValueError()
  assigned = collections.Counter(r['order_id'] for r in rows('inventory') if r['order_id'])
  migrations = sorted((r['app'], r['name']) for r in rows('django_migrations'))
  if len(set(migrations)) != len(migrations):
    raise ValueError()
  result = {'rows': {t: counts[t] for t in sorted(TABLES & counts.keys())}, 'other_tables': len(counts.keys() - TABLES), 'other_rows': sum(v for k, v in counts.items() if k not in TABLES), 'migration_set_sha256': hashlib.sha256(json.dumps(migrations, separators=(',', ':')).encode()).hexdigest()}
  result.update(nonzero_irr_wallets=tally('users', lambda r: number(r['balance_irr']) != 0), nonzero_usd_wallets=tally('users', lambda r: number(r['balance']) != 0), original_ban_times=tally('users', lambda r: r.get('banned_at')), irr_orders=tally('orders', lambda r: r['currency'] == 'IRR'), irr_transactions=tally('transactions', lambda r: r['currency'] == 'IRR'), zero_transactions=tally('transactions', lambda r: number(r['amount']) == 0), missing_checkout_snapshots=sum(not s for s in snapshots), irr_wallet_order_snapshots=sum(s.get('currency') == 'IRR' and s.get('payment_method') == 'wallet' for s in snapshots), unowned_coupons=tally('discounts', lambda r: not r['assigned_user_id']), unowned_full_discount_coupons=tally('discounts', lambda r: not r['assigned_user_id'] and number(r['pct_off']) == 100), full_discount_coupons=tally('discounts', lambda r: number(r['pct_off']) == 100), assigned_inventory=tally('inventory', lambda r: r['order_id']), unassigned_inventory=tally('inventory', lambda r: not r['order_id']), assigned_below_quantity=tally('orders', lambda r: 0 < assigned[r['id']] < int(r['qty'])), assigned_above_quantity=tally('orders', lambda r: assigned[r['id']] > int(r['qty'])), orders_without_inventory=tally('orders', lambda r: assigned[r['id']] == 0), receipt_file_references=tally('manual_deposits', lambda r: r['proof_file_path']), product_media_references=tally('products', lambda r: r['post_purchase_media_id']), unlinked_deposits=tally('manual_deposits', lambda r: not r['fulfilled_order_id']), invoice_created_attempts=tally('payment_attempts', lambda r: r['status'] == 'invoice_created'), unlinked_payment_attempts=tally('payment_attempts', lambda r: not r['fulfilled_order_id']), encrypted_inventory=tally('inventory', lambda r: r['data'].startswith('enctext:v1:')), encrypted_service_payloads=tally('service_jobs', lambda r: (r['submitted_data'] or '').startswith('encjson:v1:')))
  return result

def overlaps(sources):
  return [{'sources': [a + 1, b + 1], 'rows': {t: len({r['id'] for r in sources[a].get(t, [])} & {r['id'] for r in sources[b].get(t, [])}) for t in ('users', 'products', 'orders', 'inventory', 'manual_deposits', 'payment_attempts')}} for a, b in itertools.combinations(range(len(sources)), 2)]

def inspect(path, executable):
  original = hashlib.sha256(path.read_bytes()).hexdigest()
  def read(mode):
    run = subprocess.run([executable, mode, '--file=-', '--no-owner', '--no-privileges', str(path)], capture_output=True, check=False)
    if run.returncode:
      raise ValueError()
    return run.stdout.decode('utf-8')
  schema = read('--schema-only')
  normalized = '\n'.join(l for l in schema.splitlines() if l and not l.startswith(('--', '\\restrict', '\\unrestrict')))
  tables, counts = copy_tables(read('--data-only'))
  if hashlib.sha256(path.read_bytes()).hexdigest() != original:
    raise ValueError()
  return {'archive_sha256': original, 'schema_sql_sha256': hashlib.sha256(normalized.encode()).hexdigest(), 'tables': len(counts), 'decoded': True, 'restored': False, **inventory(tables, counts)}, tables

def main():
  parser = argparse.ArgumentParser()
  parser.add_argument('--pg-restore', default='pg_restore')
  parser.add_argument('archives', type=pathlib.Path, nargs='+')
  args = parser.parse_args()
  try:
    results = [inspect(p, args.pg_restore) for p in args.archives]
    print(json.dumps({'scope': 'decoded_archive_inventory_only', 'release_ready': False, 'sources': [r[0] for r in results], 'identity_collisions': overlaps([r[1] for r in results])}, sort_keys=True, indent=2))
  except (OSError, ValueError, decimal.InvalidOperation, KeyError, TypeError, OverflowError):
    print('Legacy archive inspection rejected; retain protected inputs for review.', file=sys.stderr)
    return 1
  return 0

if __name__ == '__main__':
  sys.exit(main())
