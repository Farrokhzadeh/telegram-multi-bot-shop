import contextlib
import io
import importlib.util
import json
import pathlib
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('evidence', pathlib.Path(__file__).parents[1] / 'inspect-legacy-order-evidence.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

class LegacyOrderEvidenceTests(unittest.TestCase):
  def test_source_associations_are_counted_once_and_redacted(self):
    rows = {'orders':[{'id':'PRIVATE-ORDER','user_id':'PRIVATE-USER','qty':'2','currency':'IRR','total':'20','checkout_snapshot':'{}'},{'id':'OTHER','user_id':'U2','qty':'1','currency':'USD','total':'2','checkout_snapshot':'{}'},{'checkout_snapshot':'{"version":1}'}], 'transactions':[{'id':'PRIVATE-ORDER','user_id':'PRIVATE-USER','currency':'IRR','amount':'-20'}], 'manual_deposits':[{'fulfilled_order_id':'PRIVATE-ORDER','user_id':'PRIVATE-USER','checkout_snapshot':'{"version":1}'},{'fulfilled_order_id':'PRIVATE-ORDER','user_id':'OTHER-USER','checkout_snapshot':'{}'}], 'payment_attempts':[], 'inventory':[{'order_id':'PRIVATE-ORDER'}]}
    result = MODULE.analyze(rows)
    self.assertEqual((2,1,1,1,1,1,1), tuple(result[k] for k in ('empty','linked_same_user','linked_different_user','linked_snapshot_same_user','transaction_exact','inventory_partial','inventory_none')))
    self.assertNotIn('PRIVATE', json.dumps(result))
    rows['transactions'][0]['currency'] = 'USD'
    self.assertEqual(0, MODULE.analyze(rows)['transaction_exact'])
    rows['transactions'][0]['currency'] = 'IRR'
    rows['transactions'][0]['user_id'] = 'OTHER-USER'
    self.assertEqual(0, MODULE.analyze(rows)['transaction_exact'])
  def test_cli_includes_validation_and_never_prints_partial_evidence_on_failure(self):
    tables = {'orders':[],'transactions':[],'manual_deposits':[],'payment_attempts':[],'inventory':[],'users':[],'products':[]}
    with patch.object(MODULE.sys, 'argv', ['inspect','PRIVATE-ARCHIVE']), patch.object(MODULE.INVENTORY, 'inspect', return_value=({'archive_sha256':'a'*64},tables)), contextlib.redirect_stdout(io.StringIO()) as output:
      self.assertEqual(0, MODULE.main())
      result = json.loads(output.getvalue())
      self.assertEqual(0, result['sources'][0]['linked_snapshot_validation']['representable_by_current_importer'])
      self.assertNotIn('PRIVATE', output.getvalue())
    with patch.object(MODULE.sys, 'argv', ['inspect','PRIVATE-A','PRIVATE-B']), patch.object(MODULE.INVENTORY, 'inspect', side_effect=[({'archive_sha256':'a'*64},tables),ValueError('PRIVATE-ROW')]), contextlib.redirect_stdout(io.StringIO()) as output, contextlib.redirect_stderr(io.StringIO()) as errors:
      self.assertEqual(1, MODULE.main())
      self.assertEqual('', output.getvalue()); self.assertNotIn('PRIVATE', errors.getvalue())
  def test_invalid_linked_snapshot_and_duplicate_transactions_reject(self):
    rows = {'orders':[],'transactions':[],'manual_deposits':[{'fulfilled_order_id':'o','checkout_snapshot':'[]'}],'payment_attempts':[],'inventory':[]}
    with self.assertRaises(ValueError):
      MODULE.analyze(rows)
    rows['manual_deposits'] = []
    rows['transactions'] = [{'id':'same'},{'id':'same'}]
    with self.assertRaises(ValueError):
      MODULE.analyze(rows)

if __name__ == '__main__':
  unittest.main()
