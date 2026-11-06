import copy
import json
import hashlib
import hmac
import importlib.util
import pathlib
import unittest
from unittest.mock import patch, MagicMock

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
  def test_unowned_networked_or_unbounded_containers_reject_before_database_mutations(self):
    root=pathlib.Path('/tmp/teleg-source-qualification-synthetic');archive=MagicMock();archive.stat.return_value.st_size=MODULE.ASSETS.BRANDS['prod1'][3]
    info={'Image':MODULE.IMAGE,'Config':{'Labels':{'teleg.purpose':'source-copy-inspection'},'User':'70:70'},'Mounts':[{'Type':'bind','Destination':'/q','Source':str(root/'socket')},{'Type':'bind','Destination':'/var/lib/postgresql/data','Source':str(root/'data')}],'HostConfig':{'NetworkMode':'none','PortBindings':{},'Memory':201326592,'MemorySwap':201326592,'NanoCpus':500000000,'PidsLimit':64,'ReadonlyRootfs':True,'Privileged':False,'CapDrop':['ALL'],'SecurityOpt':['no-new-privileges']}}
    for field,value in (('NetworkMode','host'),('Memory',0),('MemorySwap',-1),('NanoCpus',0),('PidsLimit',0),('ReadonlyRootfs',False),('Privileged',True),('CapDrop',[]),('SecurityOpt',[])):
      changed=copy.deepcopy(info);changed['HostConfig'][field]=value
      with self.subTest(field=field),patch.object(MODULE.ASSETS,'digest',return_value=MODULE.ASSETS.BRANDS['prod1'][2]),patch.object(MODULE,'run',return_value=json.dumps([changed])) as run:
        with self.assertRaises(ValueError):MODULE.prepare(root,'teleg-source-copy-synthetic','prod1',archive)
        self.assertEqual(1,run.call_count);self.assertEqual(['docker','inspect','teleg-source-copy-synthetic'],run.call_args.args[0])
    for identity in ([],[['1','prod2','production']],[['1','prod1','staging']]):
      with self.subTest(identity=identity),patch.object(MODULE.ASSETS,'digest',return_value=MODULE.ASSETS.BRANDS['prod1'][2]),patch.object(MODULE,'run',side_effect=[json.dumps([info]),b'',b'',b'']) as run,patch.object(MODULE,'rows',return_value=identity):
        with self.assertRaises(ValueError):MODULE.prepare(root,'teleg-source-copy-synthetic','prod1',archive)
        self.assertEqual(4,run.call_count)
  def test_identifier_injection_is_rejected_before_any_process_starts(self):
    with patch.object(MODULE.subprocess,'Popen') as process:
      with self.assertRaises(ValueError):
        MODULE.table_commitment('owned','copy','users;DELETE','id',bytes(32))
      process.assert_not_called()

if __name__=='__main__':
  unittest.main()
