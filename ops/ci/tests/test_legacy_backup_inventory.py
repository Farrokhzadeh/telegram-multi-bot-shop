import contextlib
import importlib.util
import io
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('legacy_inventory', pathlib.Path(__file__).parents[1] / 'inspect-legacy-backups.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

class LegacyBackupInventoryTests(unittest.TestCase):
  def test_copy_escapes_nulls_empty_tables_and_termination(self):
    tables, counts = MODULE.copy_tables('COPY public.transactions (id, "desc", amount) FROM stdin;\na\tprivate\\ttext\\nnext\\\\end\t-1.20\nb\t\\N\t0\n\\.\nCOPY public.orders (id) FROM stdin;\n\\.\n')
    self.assertEqual({'transactions': 2, 'orders': 0}, counts)
    self.assertEqual('private\ttext\nnext\\end', tables['transactions'][0]['desc'])
    self.assertIsNone(tables['transactions'][1]['desc'])
    self.assertEqual(0, MODULE.copy_tables('COPY public.manual_deposits (proof_sha256) FROM stdin;\n\\.\n')[1]['manual_deposits'])
    self.assertEqual('a', MODULE.decode('\\141'))
    self.assertEqual('a', MODULE.decode('\\x61'))
  def test_truncated_duplicate_malformed_and_unexpected_copy_reject(self):
    for sql in ('COPY public.orders (id) FROM stdin;\na\n', 'COPY public.orders (id) FROM stdin;\na\tb\n\\.\n', 'COPY public.orders (id, id) FROM stdin;\n\\.\n', 'COPY public.orders (id) FROM stdin;\n\\.\nCOPY public.orders (id) FROM stdin;\n\\.\n', 'COPY private.orders (id) FROM stdin;\n\\.\n', 'no copy blocks'):
      with self.subTest(sql=sql), self.assertRaises(ValueError):
        MODULE.copy_tables(sql)
    with self.assertRaises(ValueError):
      MODULE.decode('broken\\')
  def test_missing_snapshots_irr_history_and_unowned_codes_are_separate_counts(self):
    tables = {'users': [{'id': '1', 'balance': '0', 'balance_irr': '1.00', 'banned_at': '2024-01-01'}], 'transactions': [{'id': 'x', 'currency': 'IRR', 'amount': '-1'}], 'orders': [{'id': 'o', 'qty': '2', 'currency': 'IRR', 'checkout_snapshot': '{}'}], 'discounts': [{'code': 'PRIVATE-CODE', 'pct_off': '100', 'assigned_user_id': None}]}
    result = MODULE.inventory(tables, {k: len(v) for k, v in tables.items()})
    self.assertEqual(1, result['nonzero_irr_wallets'])
    self.assertEqual(1, result['irr_transactions'])
    self.assertEqual(1, result['missing_checkout_snapshots'])
    self.assertEqual(1, result['unowned_full_discount_coupons'])
    self.assertNotIn('PRIVATE-CODE', json.dumps(result))
  def test_partial_allocation_is_evidence_without_a_delivery_or_liability_claim(self):
    tables = {'orders': [{'id': 'o', 'qty': '5', 'currency': 'USD', 'checkout_snapshot': '{}'}], 'inventory': [{'id': '1', 'order_id': 'o', 'data': 'enctext:v1:PRIVATE-CIPHER'}, {'id': '2', 'order_id': None, 'data': 'enctext:v1:PRIVATE-CIPHER'}]}
    result = MODULE.inventory(tables, {'orders': 1, 'inventory': 2})
    self.assertEqual(1, result['assigned_below_quantity'])
    self.assertEqual(1, result['unassigned_inventory'])
    self.assertNotIn('PRIVATE-CIPHER', json.dumps(result))
  def test_collisions_count_sources_without_exporting_identifiers(self):
    result = MODULE.overlaps([{'users': [{'id': 'PRIVATE-ID'}]}, {'users': [{'id': 'PRIVATE-ID'}]}])
    self.assertEqual(1, result[0]['rows']['users'])
    self.assertNotIn('PRIVATE-ID', json.dumps(result))
  def test_unmapped_table_names_and_rows_are_only_aggregate_counts(self):
    tables, counts = MODULE.copy_tables('COPY public.private_secret (id) FROM stdin;\nPRIVATE-ROW\n\\.\n')
    result = MODULE.inventory(tables, counts)
    self.assertEqual(1, result['other_tables'])
    self.assertEqual(1, result['other_rows'])
    self.assertNotIn('private_secret', json.dumps(result))
    self.assertNotIn('PRIVATE-ROW', json.dumps(result))
  def test_native_decoder_is_output_only_and_input_bytes_are_preserved(self):
    with tempfile.TemporaryDirectory() as folder:
      path = pathlib.Path(folder) / 'copy.dump'
      path.write_bytes(b'PGDMP synthetic')
      with patch.object(MODULE.subprocess, 'run') as run:
        run.side_effect = [type('Result', (), {'returncode': 0, 'stdout': b'-- header\n\\restrict arbitrary\nCREATE TABLE example();\n\\unrestrict arbitrary\n'}), type('Result', (), {'returncode': 0, 'stdout': b'COPY public.orders (id) FROM stdin;\n\\.\n'})]
        result, _ = MODULE.inspect(path, 'pg_restore')
        self.assertFalse(result['restored'])
        self.assertTrue(result['decoded'])
        self.assertEqual(b'PGDMP synthetic', path.read_bytes())
        for call in run.call_args_list:
          self.assertIn('--file=-', call.args[0])
          self.assertNotIn('--dbname', call.args[0])
  def test_failures_do_not_print_source_details_or_subprocess_errors(self):
    with patch.object(MODULE.sys, 'argv', ['inspect', 'PRIVATE-FILE']), patch.object(MODULE, 'inspect', side_effect=ValueError('PRIVATE-ROW')), contextlib.redirect_stdout(io.StringIO()) as output, contextlib.redirect_stderr(io.StringIO()) as errors:
      self.assertEqual(1, MODULE.main())
      self.assertEqual('', output.getvalue())
      self.assertNotIn('PRIVATE', errors.getvalue())
  def test_nonfinite_money_and_nonobject_snapshots_reject(self):
    for value in ('NaN', 'Infinity', '-Infinity'):
      with self.assertRaises(ValueError):
        MODULE.number(value)
    with self.assertRaises(ValueError):
      MODULE.inventory({'orders': [{'checkout_snapshot': '[]'}]}, {'orders': 1})

if __name__ == '__main__':
  unittest.main()
