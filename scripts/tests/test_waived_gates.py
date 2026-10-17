"""scripts/waived-gates.sh: which gates were waived on the way to a promotion.

A waiver is a `Gate-Waived:` line on a commit. The script must find every one in source..target, name the gate
and the reason, ignore everything outside the range, and never turn an unreadable range into "nothing waived".
"""
import unittest

from gate_tool_support import ToolCase

SCRIPT = 'waived-gates.sh'


class WaivedGates(ToolCase):
    def setUp(self):
        super().setUp()
        self.commit('base')
        self.git('branch', 'prod')                                      # the source: what prod already has
        self.git('checkout', '-q', '-b', 'test')

    def merge_commit(self, subject, body=''):
        self.git('checkout', '-q', '-b', 'topic-%d' % len(self.git('branch').splitlines()), 'prod')
        self.commit('work on ' + subject)
        topic = self.git('rev-parse', '--abbrev-ref', 'HEAD')
        self.git('checkout', '-q', 'test')
        message = subject + ('\n\n' + body if body else '')
        self.git('merge', '--no-ff', '-q', '-m', message, topic)
        return self.git('rev-parse', '--short', 'HEAD')

    def test_no_waiver_is_reported_as_no_record(self):
        self.merge_commit('Merge a into test')
        out, code = self.run_tool(SCRIPT, 'prod', 'test')
        self.assertEqual(0, code, out)
        self.assertIn('no record', out)

    def test_a_waiver_is_listed_per_gate_with_its_reason_and_blocks(self):
        sha = self.merge_commit('Merge a into test', 'Gate-Waived: ci, e2e (neden: maintainer said skip)')
        out, code = self.run_tool(SCRIPT, 'prod', 'test')
        self.assertEqual(1, code, out)
        rows = [line for line in out.splitlines() if sha in line]
        self.assertEqual(2, len(rows), out)
        self.assertTrue(any(' ci ' in row for row in rows), out)
        self.assertTrue(any(' e2e ' in row for row in rows), out)
        self.assertTrue(all('maintainer said skip' in row for row in rows), out)
        self.assertNotIn('neden:', out)

    def test_the_waiver_line_followed_by_a_co_author_paragraph_is_still_found(self):
        self.merge_commit('Merge a into test',
                          'Gate-Waived: e2e (neden: no resources)\n\nCo-Authored-By: Someone <s@example.invalid>')
        out, code = self.run_tool(SCRIPT, 'prod', 'test')
        self.assertEqual(1, code, out)
        self.assertIn('e2e', out)

    def test_several_commits_with_different_gates_are_all_listed(self):
        first = self.merge_commit('Merge a into test', 'Gate-Waived: ci (neden: one)')
        self.merge_commit('Merge b into test')                          # an ordinary merge with no waiver
        third = self.merge_commit('Merge c into test', 'Gate-Waived: e2e (neden: three)')
        out, code = self.run_tool(SCRIPT, 'prod', 'test')
        self.assertEqual(1, code, out)
        rows = out.splitlines()
        self.assertTrue(any(first in row and ' ci ' in row and 'one' in row for row in rows), out)
        self.assertTrue(any(third in row and ' e2e ' in row and 'three' in row for row in rows), out)
        self.assertNotIn('Merge b', out)

    def test_ordinary_commits_without_the_line_are_not_listed(self):
        self.commit('plain commit mentioning gates and waivers in prose')
        self.commit('another one')
        out, code = self.run_tool(SCRIPT, 'prod', 'test')
        self.assertEqual(0, code, out)
        self.assertIn('no record', out)

    def test_a_waiver_the_source_already_has_is_not_in_the_range(self):
        self.git('checkout', '-q', 'prod')
        self.commit('old waiver\n\nGate-Waived: ci (neden: shipped long ago)')
        self.git('checkout', '-q', 'test')
        self.git('merge', '-q', '--no-edit', 'prod')
        out, code = self.run_tool(SCRIPT, 'prod', 'test')
        self.assertEqual(0, code, out)
        self.assertNotIn('shipped long ago', out)

    def test_a_waiver_without_a_reason_says_so(self):
        self.merge_commit('Merge a into test', 'Gate-Waived: e2e')
        out, code = self.run_tool(SCRIPT, 'prod', 'test')
        self.assertEqual(1, code, out)
        self.assertIn('(no reason written)', out)

    def test_a_waiver_on_a_non_merge_commit_counts(self):
        self.commit('fix\n\nGate-Waived: ci (neden: urgent)')
        out, code = self.run_tool(SCRIPT, 'prod', 'test')
        self.assertEqual(1, code, out)

    def test_an_unreadable_ref_is_an_error_not_no_record(self):
        out, code = self.run_tool(SCRIPT, 'prod', 'origin/nowhere')
        self.assertEqual(2, code, out)
        self.assertNotIn('no record', out)

    def test_usage(self):
        out, code = self.run_tool(SCRIPT, 'prod')
        self.assertEqual(2, code, out)


if __name__ == '__main__':
    unittest.main()
