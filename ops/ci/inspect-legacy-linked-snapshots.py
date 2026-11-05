import collections
import datetime
import decimal
import json

def snapshot(value):
  def unique(pairs):
    result = {}
    for key, value in pairs:
      if key in result:
        raise ValueError()
      result[key] = value
    return result
  result = json.loads(value, parse_float=decimal.Decimal, object_pairs_hook=unique)
  if not isinstance(result, dict):
    raise ValueError()
  return result

def number(value):
  if not isinstance(value, (str, int, decimal.Decimal)) or isinstance(value, bool):
    raise ValueError()
  result = decimal.Decimal(value)
  if not result.is_finite() or len(result.as_tuple().digits) > 29 or abs(result.as_tuple().exponent) > 28:
    raise ValueError()
  return result

def positive(value):
  result = number(value)
  if result != result.to_integral_value() or result <= 0 or result > 2147483647:
    raise ValueError()
  return int(result)

def time(value):
  result = datetime.datetime.fromisoformat(value)
  if result.tzinfo is None or result.utcoffset() != datetime.timedelta():
    raise ValueError()
  return result

def index(rows):
  result = {r['id']: r for r in rows}
  if len(result) != len(rows):
    raise ValueError()
  return result

def validate(order, payment, table, users, products):
  errors, gaps, facts = set(), {'empty_order_import_unsupported'}, set()
  def check(label, comparison):
    try:
      if comparison():
        facts.add(label)
      else:
        errors.add(label)
    except (ValueError, KeyError, TypeError, decimal.InvalidOperation, OverflowError):
      errors.add(label)
  try:
    saved = snapshot(payment['checkout_snapshot'])
  except (ValueError, TypeError):
    return {'invalid_snapshot'}, gaps, facts
  check('source_identity', lambda: order['user_id'] in users and order['product_id'] in products and payment['fulfilled_order_id'] == order['id'])
  check('ownership', lambda: payment['user_id'] == order['user_id'])
  check('product', lambda: positive(saved['product_id']) == positive(order['product_id']) == positive(payment['target_pid'] if table == 'manual_deposits' else payment['product_id']))
  check('quantity', lambda: positive(saved['quantity']) == positive(order['qty']) == positive(payment['target_qty'] if table == 'manual_deposits' else payment['quantity']))
  check('currency', lambda: saved['currency'] == order['currency'] == ('IRR' if table == 'manual_deposits' else payment['expected_currency']) and order['currency'] in ('IRR', 'USD'))
  check('amount', lambda: number(order['total']) == number(payment['amount'] if table == 'manual_deposits' else payment['expected_amount']) > 0)
  check('title', lambda: isinstance(saved['product_name'], str) and 0 < len(saved['product_name'].strip()) <= 160)
  check('product_type', lambda: saved['product_type'] in ('credential', 'service', 'api', 'preorder') and (saved['product_type'] != 'service' or positive(order['qty']) == 1))
  method = 'bank' if table == 'manual_deposits' else 'crypto'
  if 'payment_method' in saved:
    check('payment_method', lambda: saved['payment_method'] == method)
  else:
    gaps.add('missing_explicit_payment_method')
  check('creation_times', lambda: time(payment['created_at']) <= time(order['created_at']))
  if table == 'manual_deposits':
    check('payment_status', lambda: payment['status'] == 'approved')
    check('discount_reference', lambda: (saved['discount_code'] or '') == (payment.get('dcode') or ''))
    if payment.get('reviewed_at') and payment.get('reviewed_by'):
      check('timestamps', lambda: time(payment['created_at']) <= time(order['created_at']) <= time(payment['reviewed_at']))
    else:
      gaps.add('missing_review_provenance')
  else:
    check('payment_status', lambda: payment['status'] == 'fulfilled' and payment['kind'] == 'direct_checkout')
    check('discount_reference', lambda: (saved['discount_code'] or '') == (payment.get('discount_code') or ''))
    if payment.get('paid_at') and payment.get('fulfilled_at'):
      check('timestamps', lambda: time(payment['created_at']) <= time(payment['paid_at']) <= time(order['created_at']) <= time(payment['fulfilled_at']))
    else:
      gaps.add('missing_settlement_provenance')
  if 'version' not in saved:
    gaps.update(('unversioned_snapshot', 'missing_usd_fx_rounding_terms'))
    check('snapshot_amount', lambda: number(saved['total']) == number(order['total']))
    with decimal.localcontext() as context:
      context.prec = 100
      check('unit_arithmetic', lambda: number(saved['unit_price']) > 0 and number(saved['unit_price']) * positive(saved['quantity']) == number(saved['total']))
    gaps.add('unit_price_not_original_price_proof')
    if saved.get('discount_code'):
      gaps.add('missing_discount_terms')
  else:
    gaps.add('snapshot_format_requires_separate_review')
  return errors, gaps, facts

def analyze(tables):
  orders, users, products = (index(tables.get(name, [])) for name in ('orders', 'users', 'products'))
  links = collections.defaultdict(list)
  for table in ('manual_deposits', 'payment_attempts'):
    for payment in index(tables.get(table, [])).values():
      if payment.get('fulfilled_order_id'):
        links[payment['fulfilled_order_id']].append((table, payment))
  counts, errors, gaps, facts = (collections.Counter() for _ in range(4))
  for order in orders.values():
    if snapshot(order['checkout_snapshot']):
      continue
    counts['empty_orders'] += 1
    linked = links[order['id']]
    nonempty = []
    for table, payment in linked:
      try:
        present = bool(snapshot(payment['checkout_snapshot']))
      except (ValueError, TypeError):
        present = True
      if present:
        nonempty.append((table, payment))
    if not nonempty:
      counts['blocked_without_linked_snapshot'] += 1
      continue
    counts['linked_snapshot_orders'] += 1
    row_errors, row_gaps, row_facts = set(), set(), None
    if len(linked) != 1:
      row_errors.add('ambiguous_payment_links')
    for table, payment in nonempty:
      a, b, c = validate(order, payment, table, users, products)
      row_errors.update(a); row_gaps.update(b)
      row_facts = c if row_facts is None else row_facts & c
    counts['blocked_linked_orders' if row_errors else 'candidates_requiring_review'] += 1
    errors.update(row_errors); gaps.update(row_gaps); facts.update(row_facts or set())
  return {'scope':'linked_payment_snapshot_source_validation_only', 'representable_by_current_importer':0, **{k:counts[k] for k in ('empty_orders','linked_snapshot_orders','candidates_requiring_review','blocked_linked_orders','blocked_without_linked_snapshot')}, 'contradictions_or_invalid_facts':dict(sorted(errors.items())), 'missing_or_unproved_terms':dict(sorted(gaps.items())), 'matched_fact_counts':dict(sorted(facts.items())), 'release_ready':False}
