import base64
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import tempfile
import unittest
from unittest.mock import patch
from cryptography.fernet import Fernet

SPEC = importlib.util.spec_from_file_location('legacy_assets', pathlib.Path(__file__).parents[1] / 'verify-legacy-assets.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

class LegacyAssetsTests(unittest.TestCase):
  def test_existing_golden_vectors_authenticate_including_previous_keys_without_ttl(self):
    path = pathlib.Path(__file__).parents[3] / 'tests' / 'fixtures' / 'legacy-fernet-synthetic.json'
    vectors = json.loads(path.read_text())['vectors']
    for vector in vectors:
      keys = vector['keys']
      values = {'FIELD_ENCRYPTION_KEY': keys[0], 'FIELD_ENCRYPTION_KEY_PREVIOUS': keys[1] if len(keys) > 1 else ''}
      prefix = {'Text': 'enctext:v1:', 'Json': 'encjson:v1:', 'Secret': 'enc:v1:'}[vector['kind']]
      if vector['ciphertext'].startswith(prefix):
        with self.subTest(kind=vector['kind']):
          self.assertIn(MODULE.authenticate(vector['ciphertext'], prefix, MODULE.keyring(values)), ('authenticated_active', 'authenticated_previous'))
  def test_previous_key_fallback_and_missing_wrong_tampered_and_empty_values(self):
    old, new = Fernet.generate_key(), Fernet.generate_key()
    value = 'enctext:v1:' + Fernet(old).encrypt(b'PUBLIC-SYNTHETIC-CONTENT').decode()
    keys = MODULE.keyring({'FIELD_ENCRYPTION_KEY': new.decode(), 'FIELD_ENCRYPTION_KEY_PREVIOUS': 'fernet:' + old.decode()})
    self.assertEqual('authenticated_previous', MODULE.authenticate(value, 'enctext:v1:', keys))
    self.assertEqual('missing_key', MODULE.authenticate(value, 'enctext:v1:', []))
    self.assertEqual('authentication_failed', MODULE.authenticate(value, 'enctext:v1:', keys[:1]))
    self.assertEqual('authentication_failed', MODULE.authenticate(value[:-6] + 'xxxxxx', 'enctext:v1:', keys))
    self.assertEqual('unsupported_representation', MODULE.authenticate('plaintext', 'enctext:v1:', keys))
    self.assertEqual('empty', MODULE.authenticate(None, 'enctext:v1:', []))
    self.assertEqual('empty', MODULE.authenticate('', 'enctext:v1:', []))
  def test_key_derivation_matches_source_raw_secret_fallback_and_deduplication(self):
    raw = 'public synthetic unicode secret فارسی'
    key = base64.urlsafe_b64encode(hashlib.sha256(raw.encode()).digest())
    value = 'enctext:v1:' + Fernet(key).encrypt(b'synthetic').decode()
    keys = MODULE.keyring({'DJANGO_SECRET_KEY': raw, 'FIELD_ENCRYPTION_KEY_PREVIOUS': 'fernet:' + key.decode()})
    self.assertEqual(1, len(keys))
    self.assertEqual('authenticated_active', MODULE.authenticate(value, 'enctext:v1:', keys))
  def test_authenticated_json_and_utf8_encoding_are_checked_without_exporting_plaintext(self):
    key = Fernet.generate_key()
    keys = MODULE.keyring({'FIELD_ENCRYPTION_KEY': key.decode()})
    for raw in (b'not JSON PRIVATE', b'\xff'):
      value = 'encjson:v1:' + Fernet(key).encrypt(raw).decode()
      self.assertEqual('invalid_plaintext_encoding', MODULE.authenticate(value, 'encjson:v1:', keys))
    self.assertEqual('invalid_ciphertext', MODULE.authenticate('enctext:v1:فارسی', 'enctext:v1:', keys))
  def test_dotenv_is_literal_supports_quotes_and_never_imports_other_credentials(self):
    with tempfile.TemporaryDirectory() as folder:
      path = pathlib.Path(folder) / '.env'
      marker = pathlib.Path(folder) / 'SHOULD-NOT-EXIST'
      path.write_text("export FIELD_ENCRYPTION_KEY='$(touch " + str(marker) + ")'\nDJANGO_SECRET_KEY=public-synthetic # comment\nBOT_TOKEN=PRIVATE-UNRELATED-TOKEN\nFIELD_ENCRYPTION_KEY_PREVIOUS=\"public\\nprevious\"\n")
      values = MODULE.env_values(path)
      self.assertEqual('public-synthetic', values['DJANGO_SECRET_KEY'])
      self.assertEqual('public\nprevious', values['FIELD_ENCRYPTION_KEY_PREVIOUS'])
      self.assertNotIn('BOT_TOKEN', values)
      self.assertFalse(marker.exists())
      for content in ('FIELD_ENCRYPTION_KEY=a\nFIELD_ENCRYPTION_KEY=b\n', 'FIELD_ENCRYPTION_KEY=${PRIVATE}\n', 'FIELD_ENCRYPTION_KEY="unterminated\n'):
        path.write_text(content)
        with self.assertRaises(ValueError):
          MODULE.env_values(path)
  def test_fleet_paths_follow_actual_env_file_and_instance_data_without_key_mixing(self):
    with tempfile.TemporaryDirectory() as folder:
      root = pathlib.Path(folder)
      instance = root / 'instances/prod1'
      instance.mkdir(parents=True)
      app = root / 'private-app.env'
      app.write_text('FIELD_ENCRYPTION_KEY=app-key\n')
      config = instance / '.env'
      config.write_text('INSTANCE_ID=prod1\nDEPLOYMENT_STAGE=production\nINSTANCE_DATA_DIR=instances/prod1/data\nINSTANCE_ENV_FILE=private-app.env\nFIELD_ENCRYPTION_KEY=wrong-outer-key\n')
      values, media = MODULE.configuration(root, 'prod1')
      self.assertEqual('app-key', values['FIELD_ENCRYPTION_KEY'])
      self.assertEqual(instance / 'data/media', media)
      config.write_text(config.read_text().replace('INSTANCE_ID=prod1', 'INSTANCE_ID=prod3'))
      with self.assertRaises(ValueError):
        MODULE.configuration(root, 'prod1')
  def test_media_presence_hash_size_and_absence_do_not_modify_file_bytes(self):
    with tempfile.TemporaryDirectory() as folder:
      root = pathlib.Path(folder)
      (root / 'deposits').mkdir()
      path = root / 'deposits/photo.jpg'
      path.write_bytes(b'PUBLIC-SYNTHETIC-PHOTO')
      expected = hashlib.sha256(path.read_bytes()).hexdigest()
      self.assertEqual('verified_hash', MODULE.check_media(root, '/app/data/media/deposits/photo.jpg', expected, str(path.stat().st_size)))
      self.assertEqual('present_without_hash', MODULE.check_media(root, 'deposits/photo.jpg'))
      self.assertEqual('hash_mismatch', MODULE.check_media(root, 'deposits/photo.jpg', '0' * 64))
      self.assertEqual('size_mismatch', MODULE.check_media(root, 'deposits/photo.jpg', expected, '1'))
      self.assertEqual('invalid_metadata', MODULE.check_media(root, 'deposits/photo.jpg', 'invalid'))
      self.assertEqual('missing', MODULE.check_media(root, 'deposits/missing.jpg'))
      self.assertEqual(expected, hashlib.sha256(path.read_bytes()).hexdigest())
  def test_media_traversal_absolute_paths_symlinks_and_special_files_are_rejected(self):
    with tempfile.TemporaryDirectory() as folder:
      root = pathlib.Path(folder)
      (root / 'real').mkdir()
      (root / 'real/photo').write_bytes(b'synthetic')
      (root / 'linked').symlink_to(root / 'real', target_is_directory=True)
      (root / 'photo-link').symlink_to(root / 'real/photo')
      os.mkfifo(root / 'pipe')
      for reference in ('../private', '/etc/passwd', 'real/../private', 'real\\photo', 'real//photo'):
        self.assertEqual('invalid_reference', MODULE.check_media(root, reference))
      self.assertEqual('unsafe_path', MODULE.check_media(root, 'linked/photo'))
      self.assertEqual('unsafe_path', MODULE.check_media(root, 'photo-link'))
      self.assertEqual('not_regular', MODULE.check_media(root, 'pipe'))
  def test_unknown_media_and_missing_keys_remain_per_record_exceptions_without_blocking_other_checks(self):
    key = Fernet.generate_key()
    good = 'enctext:v1:' + Fernet(key).encrypt(b'PRIVATE-CREDENTIAL').decode()
    tables = {t: [] for t in MODULE.FIELDS}
    tables.update(inventory=[{'id': 'PRIVATE-ID', 'data': good}], manual_deposits=[{'id': 'PRIVATE-PAYMENT', 'proof_file_path': 'PRIVATE/missing.jpg', 'proof_sha256': None, 'proof_size_bytes': None}], products=[{'id': 'PRIVATE-PRODUCT', 'post_purchase_media_id': 'PRIVATE-TELEGRAM-REF'}])
    with tempfile.TemporaryDirectory() as folder:
      report, details = MODULE.verify(tables, {}, pathlib.Path(folder))
      self.assertEqual('needs_review', report['source_authentication'])
      self.assertEqual(1, report['receipt_files']['missing'])
      self.assertEqual(3, report['exception_records'])
      self.assertFalse(report['release_ready'])
      self.assertNotIn('PRIVATE', json.dumps(report))
      self.assertEqual('PRIVATE-ID', details[0]['source_id'])
      report, _ = MODULE.verify(tables, {'FIELD_ENCRYPTION_KEY': key.decode()}, pathlib.Path(folder))
      self.assertEqual('passed', report['source_authentication'])
      self.assertEqual(1, report['receipt_files']['missing'])
  def test_empty_crypto_scope_is_not_claimed_as_key_verification(self):
    tables = {t: [] for t in MODULE.FIELDS}
    tables.update(manual_deposits=[], products=[])
    report, _ = MODULE.verify(tables, {}, pathlib.Path('/unused'))
    self.assertEqual('not_exercised', report['source_authentication'])
    self.assertFalse(report['target_reencryption_qualified'])
  def test_pinned_archive_decoding_is_read_only_and_rejects_changed_or_missing_tables(self):
    with tempfile.TemporaryDirectory() as folder:
      path = pathlib.Path(folder) / 'database.dump'
      path.write_bytes(b'PUBLIC-SYNTHETIC-DUMP')
      expected = MODULE.digest(path)
      sql = ''.join('COPY public.' + t + ' (id) FROM stdin;\n\\.\n' for t in sorted(set(MODULE.FIELDS) | {'manual_deposits', 'products'}))
      with patch.object(MODULE.subprocess, 'run') as run:
        run.return_value = type('Result', (), {'returncode': 0, 'stdout': sql.encode()})
        self.assertEqual(8, len(MODULE.archived_tables(path, expected, 'pg_restore')))
        args = run.call_args.args[0]
        self.assertIn('--file=-', args)
        self.assertNotIn('--dbname', args)
        self.assertTrue(any(a == '--table=inventory' for a in args))
        with self.assertRaises(ValueError):
          MODULE.archived_tables(path, '0' * 64, 'pg_restore')
        run.return_value = type('Result', (), {'returncode': 0, 'stdout': b'COPY public.inventory (id) FROM stdin;\n\\.\n'})
        with self.assertRaises(ValueError):
          MODULE.archived_tables(path, expected, 'pg_restore')
        def changed_archive(*args, **kwargs):
          path.write_bytes(b'CHANGED-BY-SYNTHETIC-FIXTURE')
          return type('Result', (), {'returncode': 0, 'stdout': sql.encode()})
        run.side_effect = changed_archive
        with self.assertRaises(ValueError):
          MODULE.archived_tables(path, expected, 'pg_restore')
        run.side_effect = None
        path.write_bytes(b'PUBLIC-SYNTHETIC-DUMP')
        run.return_value = type('Result', (), {'returncode': 1, 'stdout': b'PRIVATE-ERROR'})
        with self.assertRaises(ValueError):
          MODULE.archived_tables(path, expected, 'pg_restore')
  def test_existing_docker_decoder_receives_archive_stdin_without_database_or_container_mutations(self):
    with tempfile.TemporaryDirectory() as folder:
      root = pathlib.Path(folder)
      path = root / 'database.dump'
      path.write_bytes(b'PUBLIC-SYNTHETIC-DUMP')
      sql = ''.join('COPY public.' + t + ' (id) FROM stdin;\n\\.\n' for t in sorted(set(MODULE.FIELDS) | {'manual_deposits', 'products'}))
      with patch.object(MODULE.subprocess, 'run', return_value=type('Result', (), {'returncode': 0, 'stdout': sql.encode()})) as run:
        MODULE.archived_tables(path, MODULE.digest(path), 'pg_restore', root)
        args = run.call_args.args[0]
        self.assertIn('exec', args)
        self.assertIn('--file=-', args)
        self.assertEqual(str(path), run.call_args.kwargs['stdin'].name)
        for forbidden in ('up', 'run', 'restart', 'stop', 'psql', '--dbname', 'sh', '-c'):
          self.assertNotIn(forbidden, args)
  def test_archive_discovery_selects_exact_bytes_not_latest_filename(self):
    with tempfile.TemporaryDirectory() as folder:
      root = pathlib.Path(folder)
      (root / 'new.dump').write_bytes(b'bad')
      good = root / 'old.dump'
      good.write_bytes(b'yes')
      with patch.object(MODULE, 'BRANDS', {'prod1': ('synthetic', 'iran', MODULE.digest(good), 3)}):
        self.assertEqual({'prod1': good}, MODULE.find_archives([root], ['prod1']))
        good.write_bytes(b'changed')
        self.assertEqual({}, MODULE.find_archives([root], ['prod1']))
  def test_report_files_are_private_and_cannot_overwrite_an_existing_file(self):
    with tempfile.TemporaryDirectory() as folder:
      root = pathlib.Path(folder)
      MODULE.write_report(root, 'summary.json', {'release_ready': False})
      self.assertEqual(0o600, (root / 'summary.json').stat().st_mode & 0o777)
      with self.assertRaises(FileExistsError):
        MODULE.write_report(root, 'summary.json', {})
  def test_main_keeps_failed_instances_separate_and_stdout_contains_no_secrets(self):
    with tempfile.TemporaryDirectory() as folder:
      root = pathlib.Path(folder)
      fleet = root / 'fleet'
      fleet.mkdir()
      good = {'source_authentication': 'not_exercised', 'exception_records': 0, 'product_media_references_unverified': 0, 'release_ready': False}
      argv = ['verify', '--fleet-root', str(fleet), '--instance', 'prod1', '--instance', 'prod3', '--output-parent', str(root / 'reports')]
      with patch.object(MODULE.sys, 'argv', argv), patch.object(MODULE.shutil, 'which', return_value='/usr/bin/pg_restore'), patch.object(MODULE, 'find_archives', return_value={'prod1': root / 'copy.dump'}), patch.object(MODULE, 'configuration', return_value=({'FIELD_ENCRYPTION_KEY': 'PRIVATE-KEY'}, root)), patch.object(MODULE, 'archived_tables', return_value={}), patch.object(MODULE, 'verify', return_value=(good, [])), contextlib.redirect_stdout(io.StringIO()) as output:
        self.assertEqual(1, MODULE.main())
      directory = next((root / 'reports').iterdir())
      summary = json.loads((directory / 'summary.json').read_text())
      self.assertTrue(summary['instances']['prod1']['inspection_complete'])
      self.assertFalse(summary['instances']['prod3']['inspection_complete'])
      self.assertEqual(['prod2'], summary['not_inspected'])
      self.assertNotIn('PRIVATE', output.getvalue() + (directory / 'summary.json').read_text())
      self.assertEqual(0o700, directory.stat().st_mode & 0o777)

if __name__ == '__main__':
  unittest.main()
