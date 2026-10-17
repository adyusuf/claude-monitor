"""scripts/ci-status.sh: what a workflow said about a commit.

The one property that matters: only a COMPLETED, SUCCESSFUL run of exactly this commit is a pass. A run that is
still going, a red one, no run at all, and a missing or failing gh each get their own non-zero answer and say why.
"""
import json
import unittest

from gate_tool_support import ToolCase

SCRIPT = 'ci-status.sh'


class CiStatus(ToolCase):
    def setUp(self):
        super().setUp()
        self.sha = self.commit('first')
        self.other = self.commit('second')

    def gh(self, runs, rc=0):
        """Install a fake gh that answers `gh run list` with these runs (headSha defaults to the first commit)."""
        payload = json.dumps([dict({'headSha': self.sha, 'databaseId': 1, 'createdAt': '2026-10-17T10:00:00Z',
                                    'url': 'https://example.invalid/run/1'}, **r) for r in runs])
        self.write('gh-answer.json', payload)
        self.fake('gh', body='cat "%s/gh-answer.json"' % self.root, rc=rc)

    def test_a_completed_successful_run_passes(self):
        self.gh([{'status': 'completed', 'conclusion': 'success'}])
        out, code = self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertEqual(0, code, out)
        self.assertIn('PASSED', out)

    def test_a_failed_run_is_red(self):
        self.gh([{'status': 'completed', 'conclusion': 'failure'}])
        out, code = self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertEqual(1, code, out)
        self.assertIn("finished as 'failure'", out)

    def test_a_cancelled_run_is_not_a_pass(self):
        self.gh([{'status': 'completed', 'conclusion': 'cancelled'}])
        out, code = self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertEqual(1, code, out)

    def test_a_run_in_progress_is_not_a_pass(self):
        self.gh([{'status': 'in_progress', 'conclusion': ''}])
        out, code = self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertEqual(3, code, out)
        self.assertIn('NOT FINISHED', out)
        self.assertIn("is 'in_progress' — https://example.invalid/run/1", out)   # the run's address survives an empty conclusion

    def test_a_queued_run_is_not_a_pass(self):
        self.gh([{'status': 'queued', 'conclusion': None}])
        out, code = self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertEqual(3, code, out)

    def test_no_run_at_all_is_not_a_pass(self):
        self.gh([])
        out, code = self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertEqual(4, code, out)
        self.assertIn('NO RUN', out)

    def test_a_missing_gh_is_not_a_pass(self):
        out, code = self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertEqual(5, code, out)
        self.assertIn('gh is not installed', out)

    def test_a_gh_that_cannot_answer_is_not_a_pass(self):
        self.fake('gh', body='echo "HTTP 401: bad credentials"', rc=1)
        out, code = self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertEqual(5, code, out)
        self.assertIn('bad credentials', out)

    def test_an_answer_that_is_not_json_is_not_a_pass(self):
        self.fake('gh', body='echo "<html>"')
        out, code = self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertEqual(5, code, out)

    def test_only_a_run_of_the_asked_commit_counts(self):
        self.gh([{'status': 'completed', 'conclusion': 'success', 'headSha': self.other}])
        out, code = self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertEqual(4, code, out)

    def test_the_asked_commit_is_passed_to_gh_in_full_and_decides(self):
        self.gh([{'status': 'completed', 'conclusion': 'success', 'headSha': self.other},
                 {'status': 'completed', 'conclusion': 'failure', 'headSha': self.sha, 'databaseId': 2}])
        out, code = self.run_tool(SCRIPT, 'dev', self.sha[:8])
        self.assertEqual(1, code, out)
        self.assertIn('--commit %s' % self.sha, self.calls()[0])
        self.assertIn('--branch dev', self.calls()[0])

    def test_the_newest_run_of_the_commit_decides(self):
        old_red = {'status': 'completed', 'conclusion': 'failure', 'createdAt': '2026-10-17T09:00:00Z'}
        new_green = {'status': 'completed', 'conclusion': 'success', 'createdAt': '2026-10-17T11:00:00Z'}
        self.gh([old_red, new_green])
        self.assertEqual(0, self.run_tool(SCRIPT, 'dev', self.sha)[1])
        old_green = dict(old_red, conclusion='success')
        new_red = dict(new_green, conclusion='failure')
        self.gh([new_red, old_green])
        self.assertEqual(1, self.run_tool(SCRIPT, 'dev', self.sha)[1])

    def test_the_workflow_defaults_to_ci_and_e2e_is_selectable(self):
        self.gh([{'status': 'completed', 'conclusion': 'success'}])
        self.run_tool(SCRIPT, 'dev', self.sha)
        self.assertIn('--workflow ci.yml', self.calls()[0])
        out, code = self.run_tool(SCRIPT, 'test', self.sha, '--workflow', 'e2e')
        self.assertEqual(0, code, out)
        self.assertIn('--workflow e2e.yml', self.calls()[1])
        self.assertIn('e2e on', out)

    def test_without_a_commit_the_tip_of_the_remote_branch_is_asked(self):
        self.git('update-ref', 'refs/remotes/origin/dev', self.sha)     # the local branch is one commit ahead
        self.gh([{'status': 'completed', 'conclusion': 'success'}])
        out, code = self.run_tool(SCRIPT, 'dev')
        self.assertEqual(0, code, out)
        self.assertIn('--commit %s' % self.sha, self.calls()[0])

    def test_usage_errors(self):
        self.gh([])
        for args in ((), ('dev', '--workflow', 'deploy'), ('dev', '--bogus'), ('dev', self.sha, 'extra')):
            out, code = self.run_tool(SCRIPT, *args)
            self.assertEqual(2, code, '%s: %s' % (args, out))


if __name__ == '__main__':
    unittest.main()
