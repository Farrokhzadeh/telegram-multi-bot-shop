import hashlib
import hmac
import importlib.util
import pathlib
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('source_copy',pathlib.Path(__file__).parents[1]/'prepare-legacy-source-copy.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

class LegacySourceCopyTests(unittest.TestCase):
  def test_binary_writer_strings_use_utf8_byte_count_and_seven_bit_length(self):
    self.assertEqual(b'\x02\xc3\xa9',MODULE.binary_text('é'))
    self.assertEqual(b'\x80\x01'+b'x'*128,MODULE.binary_text('x'*128))
  def test_independent_table_vector_matches_csharp_fixture_without_exporting_rows(self):
    value=hmac.new(bytes(range(32)),digestmod=hashlib.sha256)
    for text in ('accabad-source-table-v1','users','id','1','{"id": 1, "value": "PRIVATE"}'):
      MODULE.append(value,text)
    self.assertEqual('3446cc39207eca1a5bcadcc485d59df758cdf14faf8ca91736265fa150b07fe2',value.hexdigest())
  def test_schema_queries_preserve_duplicate_expression_names_and_column_order(self):
    with patch.object(MODULE,'run',return_value='{"c0":"one","c1":"two"}\n') as run:
      self.assertEqual([['one','two']],MODULE.rows('owned','copy','SELECT 1,2',2))
      self.assertIn('q(c0,c1)',run.call_args.args[0][-1])
  def test_identifier_injection_is_rejected_before_any_process_starts(self):
    with patch.object(MODULE.subprocess,'Popen') as process:
      with self.assertRaises(ValueError):
        MODULE.table_commitment('owned','copy','users;DELETE','id',bytes(32))
      process.assert_not_called()

if __name__=='__main__':
  unittest.main()
