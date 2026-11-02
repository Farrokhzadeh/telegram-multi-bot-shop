import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("evidence", Path(__file__).resolve().parents[1] / "check-acceptance-evidence.py")
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)


class AcceptanceEvidenceTests(unittest.TestCase):
    def parse(self, counters='', outcome='Passed', definition=True, repeat=False):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'test.trx'
            row = f'<UnitTestResult testId="test" executionId="run" outcome="{outcome}" />'
            definitions = '<TestDefinitions><UnitTest id="test"><TestMethod className="Platform.UnitTests.PublicTests" name="PublicTest" /></UnitTest></TestDefinitions>' if definition else ''
            path.write_text(f'<TestRun><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" {counters}/></ResultSummary>{definitions}<Results>{row}{row if repeat else ""}</Results></TestRun>')
            return evidence.read_trx(path)

    def test_observation_never_promotes_synthetic_witnesses_to_release_or_human_acceptance(self):
        result = self.parse()
        rows = [{'criterion': 'SC-001', 'criterion_sha256': 'public', 'witnesses': ['Platform.UnitTests.PublicTests.PublicTest'], 'remaining_evidence': 'Actual environment review'}, {'criterion': 'SC-008', 'criterion_sha256': 'public', 'witnesses': [], 'remaining_evidence': 'Human measurement'}]
        report = evidence.observe(rows, {suite: result for suite in evidence.SUITES})
        self.assertFalse(report['releaseReady'])
        self.assertFalse(report['fullSuccessCriteriaVerified'])
        self.assertEqual(['observed-synthetic-subset', 'planned-unexecuted'], [r['state'] for r in report['scenarios']])

    def test_failed_skipped_or_incomplete_results_cannot_be_evidence(self):
        for counters in ['failed="1"', 'notExecuted="1"', 'aborted="1"', 'warning="1"']:
            with self.subTest(counters=counters), self.assertRaises(ValueError):
                self.parse(counters=counters)
        with self.assertRaises(ValueError):
            self.parse(outcome='Failed')

    def test_summary_without_definitions_or_with_extra_rows_is_not_proof(self):
        with self.assertRaises(ValueError):
            self.parse(definition=False)
        with self.assertRaises(ValueError):
            self.parse(repeat=True)

    def test_missing_witness_or_suite_stops_report_production(self):
        result = self.parse()
        row = {'criterion': 'SC-001', 'criterion_sha256': 'public', 'witnesses': ['Platform.UnitTests.PublicTests.Missing'], 'remaining_evidence': 'Unexecuted'}
        with self.assertRaises(ValueError):
            evidence.observe([row], {suite: result for suite in evidence.SUITES})
        with self.assertRaises(ValueError):
            evidence.observe([], {'UnitTests': result})

    def test_current_catalog_covers_exactly_approved_criteria_and_executable_witnesses(self):
        rows = evidence.catalog_rows()
        self.assertEqual(53, len(rows))
        self.assertTrue(all(row['remaining_evidence'] for row in rows))


if __name__ == '__main__':
    unittest.main()
