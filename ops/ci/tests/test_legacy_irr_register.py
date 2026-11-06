import contextlib
import importlib.util
import io
import json
import os
import pathlib
import stat
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('irr_register', pathlib.Path(__file__).parents[1] / 'preserve-legacy-irr-register.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

def decoded(instance='prod1', users='99001234\t1200.00\n99001235\t0.00\n'):
  return ('COPY public.deployment_identity (id, instance_id, deployment_stage) FROM stdin;\n1\t' + instance + '\tproduction\n\\.\nCOPY public.users (id, balance_irr) FROM stdin;\n' + users + '\\.\n').encode()

class LegacyIrrRegisterTests(unittest.TestCase):
  def test_history_mode_preserves_full_native_rows_zero_entries_and_all_account_bindings(self):
    with tempfile.TemporaryDirectory() as folder:
      archive = pathlib.Path(folder) / 'copy.dump'; archive.write_bytes(b'PGDMP')
      history = b'COPY public.transactions (id, user_id, amount, currency, created_at, desc) FROM stdin;\nIRR-ONE\t99001234\t-900.2500\tIRR\t2024-01-01 00:00:00+00\tPRIVATE-ORIGINAL\nUSD-ZERO\t99001235\t0.00\tUSD\t2024-01-02 00:00:00+00\tPRIVATE-ZERO\n\\.\n'
      with patch.dict(MODULE.SOURCE.ASSETS.BRANDS, {'prod1': ('acc-abad', 'iran', 'hash', 5)}), patch.object(MODULE.SOURCE.ASSETS, 'digest', return_value='hash'), patch.object(MODULE.subprocess, 'run') as run:
        run.return_value.returncode = 0; run.return_value.stdout = decoded() + history
        value = MODULE.extract('prod1', archive, True)
        self.assertIn('--table=transactions', run.call_args.args[0]); self.assertEqual(2, len(value['wallets']))
        self.assertEqual('0.00', value['wallets'][1]['original_irr_amount'])
        self.assertEqual('-900.2500', value['history'][0]['amount']); self.assertEqual('IRR', value['history'][0]['currency']); self.assertEqual('PRIVATE-ORIGINAL', value['history'][0]['desc'])
        for malformed in (history.replace(b'99001234', b'99009999'), history.replace(b'\tIRR\t', b'\tUSD-UNKNOWN\t'), history.replace(b'\t-900.2500\t', b'\tNaN\t'), history.replace(b'00:00:00+00', b'00:00:00'), history.replace(b'USD-ZERO', b'IRR-ONE')):
          run.return_value.stdout = decoded() + malformed
          with self.assertRaises(ValueError): MODULE.extract('prod1', archive, True)
  def test_history_register_replay_reports_counts_and_cannot_reuse_the_balance_only_policy(self):
    with tempfile.TemporaryDirectory() as folder:
      path = pathlib.Path(folder) / 'history.json'; sources = {key: pathlib.Path('/unused') for key in MODULE.SOURCE.ASSETS.BRANDS}
      def extract(key, archive, history=True):
        return {'source': key, 'brand': key, 'archive_sha256': 'hash', 'source_users': 1, 'entries': [], 'wallets': [{'legacy_user_id': '99001234', 'original_irr_amount': '0'}], 'history': [{'id': 'PRIVATE-ENTRY', 'user_id': '99001234', 'amount': '0.00', 'currency': 'IRR'}]}
      with patch.object(MODULE, 'extract', side_effect=extract):
        first = MODULE.preserve(path, sources, True); self.assertEqual(first, MODULE.preserve(path, sources, True))
        self.assertEqual(2, json.loads(path.read_text())['version']); self.assertEqual(MODULE.HISTORY_POLICY, first['policy'])
        self.assertEqual(3, sum(row['preserved_history'] for row in first['sources']))
        self.assertEqual(3, sum(row['preserved_irr_history'] for row in first['sources']))
        self.assertEqual(3, sum(row['preserved_zero_history'] for row in first['sources']))
        self.assertNotIn('PRIVATE', json.dumps(first)); self.assertNotIn('99001234', json.dumps(first)); self.assertNotIn('0.00', json.dumps(first))
        with self.assertRaises(ValueError): MODULE.preserve(path, sources)
  def test_decoder_is_isolated_output_only_and_retains_exact_currency_amount_and_identity(self):
    with tempfile.TemporaryDirectory() as folder:
      archive = pathlib.Path(folder) / 'copy.dump'; archive.write_bytes(b'PGDMP')
      brand = ('acc-abad', 'iran', 'synthetic-hash', 5)
      with patch.dict(MODULE.SOURCE.ASSETS.BRANDS, {'prod1': brand}), patch.object(MODULE.SOURCE.ASSETS, 'digest', return_value='synthetic-hash'), patch.object(MODULE.subprocess, 'run') as run:
        run.return_value.returncode = 0; run.return_value.stdout = decoded()
        value = MODULE.extract('prod1', archive)
        self.assertEqual([{'legacy_user_id': '99001234', 'currency': 'IRR', 'original_amount': '1200.00'}], value['entries'])
        command = run.call_args.args[0]
        for required in ('--network=none', '--read-only', '--memory=192m', '--memory-swap=192m', '--cpus=0.5', '--pids-limit=64', '--cap-drop=ALL', '--security-opt=no-new-privileges', '--file=-', MODULE.SOURCE.IMAGE):
          self.assertIn(required, command)
        self.assertNotIn('--dbname', command); self.assertNotIn('--volume', command)
        self.assertEqual(b'PGDMP', archive.read_bytes())
  def test_wrong_source_duplicate_ownership_invalid_amount_and_missing_tables_reject(self):
    with tempfile.TemporaryDirectory() as folder:
      archive = pathlib.Path(folder) / 'copy.dump'; archive.write_bytes(b'PGDMP')
      for body in (decoded('prod2'), decoded(users='99001234\t1\n99001234\t2\n'), decoded(users='099001234\t1\n'), decoded(users='99001234\tNaN\n'), decoded().replace(b'\tproduction\n', b'\tstaging\n'), decoded().replace(b'COPY public.users', b'COPY public.transactions')):
        with self.subTest(body=body), patch.dict(MODULE.SOURCE.ASSETS.BRANDS, {'prod1': ('acc-abad', 'iran', 'hash', 5)}), patch.object(MODULE.SOURCE.ASSETS, 'digest', return_value='hash'), patch.object(MODULE.subprocess, 'run') as run:
          run.return_value.returncode = 0; run.return_value.stdout = body
          with self.assertRaises(ValueError): MODULE.extract('prod1', archive)
  def test_changed_archive_rejects_before_or_after_decode(self):
    with tempfile.TemporaryDirectory() as folder:
      archive = pathlib.Path(folder) / 'copy.dump'; archive.write_bytes(b'PGDMP')
      for hashes, calls in ((['changed'], 0), (['hash', 'changed'], 1)):
        with self.subTest(hashes=hashes), patch.dict(MODULE.SOURCE.ASSETS.BRANDS, {'prod1': ('acc-abad', 'iran', 'hash', 5)}), patch.object(MODULE.SOURCE.ASSETS, 'digest', side_effect=hashes), patch.object(MODULE.subprocess, 'run') as run:
          run.return_value.returncode = 0; run.return_value.stdout = decoded()
          with self.assertRaises(ValueError): MODULE.extract('prod1', archive)
          self.assertEqual(calls, run.call_count)
  def test_private_atomic_register_replay_redaction_and_tamper_rejection(self):
    with tempfile.TemporaryDirectory() as folder:
      path = pathlib.Path(folder) / 'manual.json'; sources = {key: pathlib.Path('/unused') for key in MODULE.SOURCE.ASSETS.BRANDS}
      def extract(key, archive):
        return {'source': key, 'brand': key, 'archive_sha256': 'hash', 'source_users': 1, 'entries': [{'legacy_user_id': '99001234', 'currency': 'IRR', 'original_amount': '1234.50'}]}
      with patch.object(MODULE, 'extract', side_effect=extract):
        first = MODULE.preserve(path, sources); original = path.read_bytes()
        self.assertEqual(first, MODULE.preserve(path, sources)); self.assertEqual(original, path.read_bytes())
        self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
        self.assertEqual(3, sum(row['preserved_nonzero_irr_balances'] for row in first['sources']))
        self.assertFalse(first['release_ready']); self.assertEqual(0, first['operational_postings'])
        for private in ('99001234', '1234.50', 'legacy_user_id', 'original_amount'):
          self.assertNotIn(private, json.dumps(first))
        path.write_bytes(original.replace(b'1234.50', b'1234.51'))
        with self.assertRaises(ValueError): MODULE.preserve(path, sources)
        self.assertNotEqual(original, path.read_bytes())
  def test_partial_failure_never_publishes_a_register_and_link_failure_rolls_back_temporary(self):
    with tempfile.TemporaryDirectory() as folder:
      path = pathlib.Path(folder) / 'manual.json'; sources = {key: pathlib.Path('/unused') for key in MODULE.SOURCE.ASSETS.BRANDS}
      with patch.object(MODULE, 'extract', side_effect=[{}, ValueError('PRIVATE')]):
        with self.assertRaises(ValueError): MODULE.preserve(path, sources)
      self.assertEqual([], list(path.parent.iterdir()))
      with patch.object(MODULE, 'extract', return_value={}), patch.object(MODULE.os, 'link', side_effect=OSError('PRIVATE')):
        with self.assertRaises(OSError): MODULE.preserve(path, sources)
      self.assertEqual([], list(path.parent.iterdir()))
  def test_permissive_directory_or_file_symlink_and_incomplete_sources_reject(self):
    with tempfile.TemporaryDirectory() as folder:
      path = pathlib.Path(folder) / 'manual.json'; sources = {key: pathlib.Path('/unused') for key in MODULE.SOURCE.ASSETS.BRANDS}
      with patch.object(MODULE, 'extract') as extract:
        os.chmod(folder, 0o755)
        with self.assertRaises(ValueError): MODULE.preserve(path, sources)
        os.chmod(folder, 0o700); path.symlink_to(path.parent / 'missing')
        with self.assertRaises(ValueError): MODULE.preserve(path, sources)
        path.unlink()
        with self.assertRaises(ValueError): MODULE.preserve(path, {'prod1': pathlib.Path('/unused')})
        extract.assert_not_called()
  def test_cli_errors_never_export_private_exception_details(self):
    with patch.object(MODULE.sys, 'argv', ['preserve', '--register', '/private', '--prod1', '/one', '--prod2', '/two', '--prod3', '/three']), patch.object(MODULE, 'preserve', side_effect=ValueError('PRIVATE-ID-1234.50')), contextlib.redirect_stdout(io.StringIO()) as output, contextlib.redirect_stderr(io.StringIO()) as errors:
      self.assertEqual(1, MODULE.main()); self.assertEqual('', output.getvalue()); self.assertNotIn('PRIVATE', errors.getvalue())

if __name__ == '__main__':
  unittest.main()
