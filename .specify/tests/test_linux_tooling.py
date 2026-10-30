#!/usr/bin/env python3
"""Offline integration checks for the pinned Spec Kit Bash command set."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import yaml


ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / '.specify/scripts/bash'
TEMPLATE = 'constitution-template'


class LinuxToolingTests(unittest.TestCase):
    def setUp(self):
        scratch = ROOT.parent / '.tooling'
        scratch.mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix='spec-kit-test-', dir=scratch)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'project with spaces'
        self.root.mkdir()
        self.feature = self.root / 'specs/001-fixture'
        self.feature.mkdir(parents=True)
        for name in ['spec.md', 'plan.md', 'tasks.md', 'research.md', 'data-model.md', 'quickstart.md']:
            (self.feature / name).write_text('APPROVED ' + name + '\n')
        (self.feature / 'contracts').mkdir()
        (self.feature / 'contracts/test.md').write_text('fixture only\n')
        self.write('.specify/feature.json', '{\n  "feature_directory": "specs/001-fixture"\n}\n')
        self.write('.specify/templates/constitution-template.md', 'CORE "quoted"\tفارسی\n\n')
        self.write('.specify/templates/plan-template.md', 'PLAN TEMPLATE\n')
        self.write('.specify/templates/tasks-template.md', 'TASKS TEMPLATE\n')

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
        return path

    def snapshot(self):
        return {str(p.relative_to(self.root)): (p.read_bytes(), p.stat().st_mtime_ns)
                for p in self.root.rglob('*') if p.is_file()}

    def run_script(self, name, *args, ok=True, extra_env=None):
        env = os.environ.copy()
        for key in ['SPECIFY_FEATURE_DIRECTORY', 'SPECIFY_FEATURE', 'SPECIFY_INIT_DIR', 'SPECIFY_FEATURE_NO_PERSIST']:
            env.pop(key, None)
        env['SPECIFY_INIT_DIR'] = str(self.root)
        env['TMPDIR'] = self.temp.name
        env.update(extra_env or {})
        result = subprocess.run(['bash', str(SCRIPTS / name), *args], cwd=self.root,
                                env=env, text=True, capture_output=True, timeout=20)
        if ok:
            self.assertEqual(result.returncode, 0, result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout)
        return result

    def resolve(self):
        return json.loads(self.run_script('resolve-template.sh', TEMPLATE, '--json').stdout)['TEMPLATE_CONTENT']

    def preset(self, name, strategy, content):
        self.write(f'.specify/presets/{name}/templates/{TEMPLATE}.md', content)
        manifest = {'provides': {'templates': [{'type': 'template', 'name': TEMPLATE,
                    'file': f'templates/{TEMPLATE}.md', 'strategy': strategy}]}}
        self.write(f'.specify/presets/{name}/preset.yml', yaml.safe_dump(manifest))

    def test_syntax_executable_modes_and_help(self):
        scripts = sorted(SCRIPTS.glob('*.sh'))
        self.assertEqual(len(scripts), 6)
        for path in scripts:
            with self.subTest(script=path.name):
                result = subprocess.run(['bash', '-n', str(path)], text=True, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertTrue(os.access(path, os.X_OK))
                if path.name != 'common.sh':
                    self.run_script(path.name, '--help')

    def test_core_json_preserves_exact_template_content(self):
        before = self.snapshot()
        expected = (self.root / '.specify/templates/constitution-template.md').read_text()
        self.assertEqual(self.resolve(), expected)
        self.assertEqual(self.snapshot(), before)

    def test_paths_only_override_does_not_rewrite_pinned_selector(self):
        before = self.snapshot()
        override = 'specs/temporary $(touch INJECTED)'
        result = self.run_script('check-prerequisites.sh', '--json', '--paths-only',
                                 extra_env={'SPECIFY_FEATURE_DIRECTORY': override})
        data = json.loads(result.stdout)
        self.assertEqual(data['FEATURE_DIR'], str(self.root / override))
        self.assertEqual(self.snapshot(), before)
        self.assertFalse((self.root / 'INJECTED').exists())

    def test_feature_selector_is_independent_of_git_branch(self):
        before = self.snapshot()
        result = self.run_script('check-prerequisites.sh', '--json', '--require-spec',
                                 '--require-tasks', '--include-tasks')
        data = json.loads(result.stdout)
        self.assertEqual(data['FEATURE_DIR'], str(self.feature))
        self.assertIn('tasks.md', data['AVAILABLE_DOCS'])
        self.assertIn('contracts/', data['AVAILABLE_DOCS'])
        self.assertEqual(self.snapshot(), before)

    def test_setup_preserves_existing_approved_plan_and_tasks(self):
        before = self.snapshot()
        plan = json.loads(self.run_script('setup-plan.sh', '--json').stdout)
        tasks = json.loads(self.run_script('setup-tasks.sh', '--json').stdout)
        self.assertEqual(plan['IMPL_PLAN'], str(self.feature / 'plan.md'))
        self.assertEqual(tasks['TASKS_TEMPLATE_CONTENT'], 'TASKS TEMPLATE\n')
        self.assertEqual(self.snapshot(), before)

    def test_project_override_wins_over_presets_and_extensions(self):
        self.preset('one', 'replace', 'PRESET\n')
        self.write(f'.specify/extensions/one/templates/{TEMPLATE}.md', 'EXTENSION\n')
        self.write(f'.specify/templates/overrides/{TEMPLATE}.md', 'OVERRIDE\n')
        self.assertEqual(self.resolve(), 'OVERRIDE\n')

    def test_extensions_precede_core_without_a_preset(self):
        self.write(f'.specify/extensions/one/templates/{TEMPLATE}.md', 'EXTENSION\n')
        self.assertEqual(self.resolve(), 'EXTENSION\n')

    def test_ordered_preset_composition_and_disabled_preset(self):
        self.preset('disabled', 'replace', 'MUST NOT WIN\n')
        self.preset('wrapper', 'wrap', 'BEFORE{CORE_TEMPLATE}AFTER')
        self.preset('tail', 'append', 'TAIL\n')
        registry = {'presets': {'disabled': {'enabled': False, 'priority': 0},
                               'wrapper': {'priority': 1}, 'tail': {'priority': 2}}}
        self.write('.specify/presets/.registry', json.dumps(registry))
        core = (self.root / f'.specify/templates/{TEMPLATE}.md').read_text()
        self.assertEqual(self.resolve(), 'BEFORE' + core + '\n\nTAIL\nAFTER')

    def test_invalid_composition_fails_without_silent_core_fallback(self):
        self.preset('broken', 'wrap', 'NO PLACEHOLDER')
        result = self.run_script('resolve-template.sh', TEMPLATE, '--json', ok=False)
        self.assertEqual(result.stdout, '')
        self.assertIn('ERROR', result.stderr)

    def test_registered_extension_priority_and_disabled_entries(self):
        self.write(f'.specify/extensions/disabled/templates/{TEMPLATE}.md', 'DISABLED\n')
        self.write(f'.specify/extensions/chosen/templates/{TEMPLATE}.md', 'CHOSEN\n')
        self.write('.specify/extensions/.registry', json.dumps({'extensions': {
            'disabled': {'enabled': False, 'priority': 1}, 'chosen': {'priority': 2}}}))
        self.assertEqual(self.resolve(), 'CHOSEN\n')

    def test_missing_python_cannot_silently_drop_manifest_composition(self):
        self.preset('one', 'wrap', 'BEFORE{CORE_TEMPLATE}AFTER')
        command = 'source "$1"; _python3_command() { return 1; }; resolve_template_content "$2" "$3"'
        result = subprocess.run(['bash', '-c', command, 'test', str(SCRIPTS / 'common.sh'),
                                 TEMPLATE, str(self.root)], text=True, capture_output=True, timeout=20)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, '')
        self.assertIn('Python 3', result.stderr)

    def test_missing_template_and_invalid_name_fail(self):
        for name in ['missing-template', '../constitution-template']:
            with self.subTest(name=name):
                result = self.run_script('resolve-template.sh', name, '--json', ok=False)
                self.assertEqual(result.stdout, '')

    def test_feature_creation_dry_run_stays_inside_disposable_fixture(self):
        before = self.snapshot()
        result = self.run_script('create-new-feature.sh', '--json', '--dry-run',
                                 '--short-name', 'linux-test', 'Offline tooling fixture')
        data = json.loads(result.stdout)
        self.assertTrue(data)
        self.assertEqual(self.snapshot(), before)

    def test_unicode_feature_dry_run_keeps_selector_unchanged(self):
        before = self.snapshot()
        result = self.run_script('create-new-feature.sh', '--json', '--dry-run',
                                 '--short-name', 'آزمایش', 'Offline Unicode fixture')
        self.assertTrue(json.loads(result.stdout))
        self.assertEqual(self.snapshot(), before)

    def test_manifests_and_shell_integration_are_consistent(self):
        options = json.loads((ROOT / '.specify/init-options.json').read_text())
        integration = json.loads((ROOT / '.specify/integration.json').read_text())
        self.assertEqual(options['script'], 'sh')
        self.assertEqual(integration['integration_settings']['generic']['script'], 'sh')
        self.assertFalse((ROOT / '.specify/scripts/powershell').exists())
        for path in (ROOT / '.specify/integrations').glob('*.manifest.json'):
            for name, digest in json.loads(path.read_text())['files'].items():
                self.assertEqual(hashlib.sha256((ROOT / name).read_bytes()).hexdigest(), digest, name)
        for path in (ROOT / '.agents/skills').glob('*/SKILL.md'):
            text = path.read_text()
            self.assertNotIn('.ps1', text)
            self.assertNotIn('scripts/powershell', text)
            metadata = yaml.safe_load(text.split('---', 2)[1])
            self.assertEqual(metadata['name'], path.parent.name)
            self.assertTrue(metadata['description'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
