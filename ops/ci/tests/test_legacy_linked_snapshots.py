import copy
import importlib.util
import json
import pathlib
import unittest

SPEC = importlib.util.spec_from_file_location('linked', pathlib.Path(__file__).parents[1] / 'inspect-legacy-linked-snapshots.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

class LegacyLinkedSnapshotTests(unittest.TestCase):
  def fixture(self):
    saved = {'product_id':1,'product_name':'PRIVATE-TITLE','product_type':'credential','quantity':2,'unit_price':'10','total':'20','currency':'IRR','discount_code':None}
    return {'users':[{'id':'PRIVATE-USER'}], 'products':[{'id':'1'}], 'orders':[{'id':'PRIVATE-ORDER','user_id':'PRIVATE-USER','product_id':'1','qty':'2','total':'20','currency':'IRR','created_at':'2024-01-01 00:01:00+00:00','checkout_snapshot':'{}'}], 'manual_deposits':[{'id':'PRIVATE-PAYMENT','fulfilled_order_id':'PRIVATE-ORDER','user_id':'PRIVATE-USER','target_pid':'1','target_qty':'2','amount':'20','status':'approved','dcode':None,'created_at':'2024-01-01 00:00:00+00:00','reviewed_at':'2024-01-01 00:02:00+00:00','reviewed_by':'PRIVATE-REVIEWER','checkout_snapshot':json.dumps(saved)}], 'payment_attempts':[]}
  def change_snapshot(self, rows, **changes):
    payment = rows['manual_deposits'][0]
    payment['checkout_snapshot'] = json.dumps({**json.loads(payment['checkout_snapshot']), **changes})
  def test_matching_associations_are_review_only_redacted_repeatable_and_immutable(self):
    rows = self.fixture(); before = copy.deepcopy(rows)
    result = MODULE.analyze(rows)
    self.assertEqual((1,1,0,0,0), tuple(result[k] for k in ('linked_snapshot_orders','candidates_requiring_review','blocked_linked_orders','blocked_without_linked_snapshot','representable_by_current_importer')))
    self.assertEqual({}, result['contradictions_or_invalid_facts'])
    self.assertEqual(1, result['matched_fact_counts']['timestamps'])
    self.assertEqual(1, result['missing_or_unproved_terms']['unit_price_not_original_price_proof'])
    self.assertFalse(result['release_ready'])
    self.assertNotIn('PRIVATE', json.dumps(result))
    self.assertEqual(result, MODULE.analyze(rows)); self.assertEqual(before, rows)
  def test_mismatched_owner_product_quantity_currency_amount_method_and_status_block(self):
    for field, value, reason in (('user_id','OTHER','ownership'),('target_pid','2','product'),('target_qty','3','quantity'),('amount','21','amount'),('status','pending','payment_status')):
      rows = self.fixture(); rows['manual_deposits'][0][field] = value
      with self.subTest(field=field):
        result = MODULE.analyze(rows)
        self.assertEqual(1, result['blocked_linked_orders']); self.assertEqual(0, result['candidates_requiring_review'])
        self.assertEqual(1, result['contradictions_or_invalid_facts'][reason])
    for changes, reason in (({'currency':'USD'},'currency'),({'payment_method':'wallet'},'payment_method'),({'total':'21'},'snapshot_amount'),({'unit_price':'9.999999999999999999999999999'},'unit_arithmetic'),({'quantity':True},'quantity'),({'product_id':2},'product')):
      rows = self.fixture(); self.change_snapshot(rows, **changes)
      with self.subTest(changes=changes):
        self.assertEqual(1, MODULE.analyze(rows)['contradictions_or_invalid_facts'][reason])
  def test_source_parent_and_ambiguous_links_never_borrow_valid_evidence(self):
    for parent in ('users','products'):
      rows = self.fixture(); rows[parent] = []
      self.assertEqual(1, MODULE.analyze(rows)['contradictions_or_invalid_facts']['source_identity'])
    rows = self.fixture(); other = copy.deepcopy(rows['manual_deposits'][0]); other['id'] = 'OTHER'; other['user_id'] = 'OTHER'
    rows['manual_deposits'].append(other)
    result = MODULE.analyze(rows)
    self.assertEqual(1, result['linked_snapshot_orders']); self.assertEqual(1, result['blocked_linked_orders'])
    self.assertEqual(1, result['contradictions_or_invalid_facts']['ambiguous_payment_links'])
    rows['manual_deposits'][1]['checkout_snapshot'] = '{}'
    self.assertEqual(1, MODULE.analyze(rows)['blocked_linked_orders'])
  def test_duplicate_primary_ids_reject_instead_of_overwriting(self):
    for table in ('orders','manual_deposits','users','products'):
      rows = self.fixture(); rows[table].append(copy.deepcopy(rows[table][0]))
      with self.subTest(table=table), self.assertRaises(ValueError):
        MODULE.analyze(rows)
  def test_timestamp_contradictions_naive_and_missing_clocks_are_distinct(self):
    for field, value in (('created_at','2024-01-01 00:03:00+00:00'),('reviewed_at','2024-01-01 00:00:30+00:00'),('reviewed_at','2024-01-01 00:02:00'),('reviewed_at','PRIVATE-INVALID')):
      rows = self.fixture(); rows['manual_deposits'][0][field] = value
      self.assertEqual(1, MODULE.analyze(rows)['contradictions_or_invalid_facts']['timestamps'])
    rows = self.fixture(); rows['manual_deposits'][0]['reviewed_at'] = None
    result = MODULE.analyze(rows)
    self.assertEqual(1, result['candidates_requiring_review']); self.assertEqual(1, result['missing_or_unproved_terms']['missing_review_provenance'])
  def test_discount_reference_is_preserved_without_guessing_terms_or_owner(self):
    rows = self.fixture(); self.change_snapshot(rows, discount_code='PRIVATE-CODE'); rows['manual_deposits'][0]['dcode'] = 'PRIVATE-CODE'
    result = MODULE.analyze(rows)
    self.assertEqual(1, result['missing_or_unproved_terms']['missing_discount_terms'])
    self.assertEqual(1, result['candidates_requiring_review']); self.assertNotIn('PRIVATE', json.dumps(result))
    rows['manual_deposits'][0]['dcode'] = 'OTHER'
    self.assertEqual(1, MODULE.analyze(rows)['contradictions_or_invalid_facts']['discount_reference'])
  def test_malformed_duplicate_snapshot_fields_nonfinite_and_float_money_fail_closed(self):
    for value in ('[]','{"currency":"IRR","currency":"USD"}','PRIVATE-BROKEN'):
      rows = self.fixture(); rows['manual_deposits'][0]['checkout_snapshot'] = value
      self.assertEqual(1, MODULE.analyze(rows)['contradictions_or_invalid_facts']['invalid_snapshot'])
    for value in ('NaN','Infinity','1e999',True,0.1):
      with self.subTest(value=value), self.assertRaises(ValueError):
        MODULE.number(value)
    rows = self.fixture(); self.change_snapshot(rows, unit_price='NaN')
    self.assertEqual(1, MODULE.analyze(rows)['blocked_linked_orders'])
  def test_no_linked_snapshot_is_blocked_and_current_snapshots_are_outside_cohort(self):
    rows = self.fixture(); rows['manual_deposits'][0]['checkout_snapshot'] = '{}'
    result = MODULE.analyze(rows)
    self.assertEqual(1, result['blocked_without_linked_snapshot']); self.assertEqual(0, result['linked_snapshot_orders'])
    rows['orders'][0]['checkout_snapshot'] = '{"version":1}'
    self.assertEqual(0, MODULE.analyze(rows)['empty_orders'])
  def test_crypto_link_checks_its_own_origin_currency_method_and_clocks(self):
    rows = self.fixture(); rows['orders'][0].update(currency='USD')
    payment = rows['manual_deposits'].pop(); saved = json.loads(payment['checkout_snapshot']); saved.update(currency='USD',payment_method='crypto',version=1)
    payment.update(product_id='1',quantity='2',expected_amount='20',expected_currency='USD',status='fulfilled',kind='direct_checkout',discount_code=None,paid_at='2024-01-01 00:00:30+00:00',fulfilled_at='2024-01-01 00:02:00+00:00',checkout_snapshot=json.dumps(saved))
    rows['payment_attempts'] = [payment]
    result = MODULE.analyze(rows)
    self.assertEqual(1, result['candidates_requiring_review']); self.assertEqual(0, result['representable_by_current_importer'])
    payment['kind'] = 'wallet_topup'
    self.assertEqual(1, MODULE.analyze(rows)['contradictions_or_invalid_facts']['payment_status'])

if __name__ == '__main__':
  unittest.main()
