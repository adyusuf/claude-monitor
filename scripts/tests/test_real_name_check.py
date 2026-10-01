import os
import shutil
import subprocess
import tempfile
import unittest

SCRIPTS = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))

# The shell scripts are run FROM THEIR REAL PATH, not from the copy inside the
# throwaway repository. Both resolve their root with `git rev-parse
# --show-toplevel`, so the cwd decides what they inspect and behaviour is
# unchanged — but a coverage tracer attributes execution to the file it ran, so a
# copy leaves the original at 0%. The copies stay: `pre-commit.sh --install`
# symlinks to scripts/ inside the repository under test and needs them there.
CHECK = os.path.join(SCRIPTS, 'real-name-check.sh')
MAP = (
    '# comment\n'
    'Zorbexico\tryan\tzorbex\n'          # a secret name with an alias
    'OwnRepo\tjudge\t\tpublic\n'         # not secret: never searched for
    'Abc\ttiny\n'                        # three characters: too short to search for
    'Scratch*\tpicasso\n'                # a prefix key: skipped
)


def git(root, *args, check=True):
    return subprocess.run(['git', '-C', root, *args], capture_output=True, text=True, check=check)


class Repo(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        git(self.root, 'init', '-q')
        git(self.root, 'config', 'user.email', 't@example.invalid')
        git(self.root, 'config', 'user.name', 't')
        os.makedirs(os.path.join(self.root, 'scripts'))
        os.makedirs(os.path.join(self.root, 'docs'))
        for name in ('real-name-check.sh', 'pre-commit.sh', 'commit-msg.sh'):
            shutil.copy(os.path.join(SCRIPTS, name), os.path.join(self.root, 'scripts', name))
        self.write('docs/project-nicknames.tsv', MAP)
        git(self.root, 'add', 'scripts')          # the map itself stays UNstaged, as it is git-ignored in real life

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def write(self, path, text):
        full = os.path.join(self.root, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, 'w', encoding='utf-8') as handle:
            handle.write(text)

    def stage(self, path, text):
        self.write(path, text)
        git(self.root, 'add', path)

    def check(self, *args, env=None):
        return subprocess.run(['bash', CHECK, *args],
                              cwd=self.root, capture_output=True, text=True, env=dict(os.environ, **(env or {})))

    def staged(self):
        return self.check('--staged')

    def message(self, text):
        path = os.path.join(self.root, 'MSG')
        with open(path, 'w', encoding='utf-8') as handle:
            handle.write(text)
        return self.check('--message', path)


class StagedContent(Repo):
    def test_a_real_name_in_an_added_line_is_blocked_with_file_and_line(self):
        self.stage('notes.md', 'fine\nthe Zorbexico rollout\n')
        result = self.staged()
        self.assertEqual(result.returncode, 1)
        self.assertIn('notes.md:2', result.stderr)

    def test_matching_ignores_case_and_finds_an_alias(self):
        self.stage('a.md', 'ZORBEXICO here\n')
        self.assertEqual(self.staged().returncode, 1)
        git(self.root, 'reset', '-q')
        self.stage('b.md', 'see zorbex for details\n')
        self.assertEqual(self.staged().returncode, 1)

    def test_a_real_name_in_a_file_name_is_blocked(self):
        self.stage('docs/zorbexico-notes.md', 'harmless text\n')
        result = self.staged()
        self.assertEqual(result.returncode, 1)
        self.assertIn('FILE NAME', result.stderr)

    def test_only_added_lines_count_so_removing_a_leak_is_allowed(self):
        self.stage('notes.md', 'the Zorbexico rollout\nsecond line\n')
        git(self.root, 'commit', '-q', '-m', 'seed', '--no-verify')
        self.stage('notes.md', 'the project-a rollout\nsecond line\n')
        self.assertEqual(self.staged().returncode, 0)

    def test_an_old_leak_on_an_unchanged_line_does_not_block_an_unrelated_edit(self):
        self.stage('notes.md', 'the Zorbexico rollout\nsecond line\n')
        git(self.root, 'commit', '-q', '-m', 'seed', '--no-verify')
        self.stage('notes.md', 'the Zorbexico rollout\nsecond line changed\n')
        self.assertEqual(self.staged().returncode, 0)

    def test_a_public_name_a_short_term_and_a_prefix_key_are_not_searched_for(self):
        self.stage('a.md', 'OwnRepo is this repo. abc is short. Scratch things.\n')
        self.assertEqual(self.staged().returncode, 0)

    def test_staging_the_map_itself_is_blocked(self):
        git(self.root, 'add', '-f', 'docs/project-nicknames.tsv')
        self.assertEqual(self.staged().returncode, 1)

    def test_a_clean_change_passes(self):
        self.stage('a.md', 'nothing secret here\n')
        self.assertEqual(self.staged().returncode, 0)


class Message(Repo):
    def test_a_real_name_in_the_message_is_blocked(self):
        result = self.message('feat: roll the gate into Zorbexico\n')
        self.assertEqual(result.returncode, 1)
        self.assertIn('COMMIT MESSAGE', result.stderr)

    def test_git_comment_lines_in_the_message_are_ignored(self):
        self.assertEqual(self.message('feat: fine\n\n# Zorbexico is only in a comment\n').returncode, 0)

    def test_a_clean_message_passes(self):
        self.assertEqual(self.message('feat: roll the gate into project A\n').returncode, 0)


class MissingMap(Repo):
    def test_a_missing_map_warns_and_passes_because_the_check_did_not_run(self):
        os.remove(os.path.join(self.root, 'docs', 'project-nicknames.tsv'))
        self.stage('a.md', 'Zorbexico\n')
        result = self.staged()
        self.assertEqual(result.returncode, 0)
        self.assertIn('NOT RUN', result.stderr)

    def test_strict_mode_makes_a_missing_map_a_failure(self):
        os.remove(os.path.join(self.root, 'docs', 'project-nicknames.tsv'))
        self.assertEqual(self.check('--staged', env={'REAL_NAMES_STRICT': '1'}).returncode, 1)

    def test_the_map_of_the_main_worktree_is_found_from_a_linked_worktree(self):
        self.stage('seed.md', 'x\n')
        git(self.root, 'commit', '-q', '-m', 'seed', '--no-verify')
        linked = tempfile.mkdtemp()
        shutil.rmtree(linked)
        try:
            git(self.root, 'worktree', 'add', '-q', '-b', 'side', linked)
            with open(os.path.join(linked, 'leak.md'), 'w', encoding='utf-8') as handle:
                handle.write('Zorbexico\n')
            git(linked, 'add', 'leak.md')
            result = subprocess.run(['bash', CHECK, '--staged'],
                                    cwd=linked, capture_output=True, text=True)
            self.assertEqual(result.returncode, 1)         # the map lives only in the main worktree
        finally:
            git(self.root, 'worktree', 'remove', '--force', linked, check=False)
            shutil.rmtree(linked, ignore_errors=True)


class Hooks(Repo):
    def commit(self, message):
        return subprocess.run(['git', '-C', self.root, 'commit', '-q', '-m', message], capture_output=True, text=True)

    def install(self):
        result = subprocess.run(['bash', os.path.join(self.root, 'scripts', 'pre-commit.sh'), '--install'],
                                cwd=self.root, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def test_install_links_both_hooks(self):
        result = self.install()
        self.assertIn('commit-msg', result.stdout)
        for hook in ('pre-commit', 'commit-msg'):
            self.assertTrue(os.path.islink(os.path.join(self.root, '.git', 'hooks', hook)))

    def test_the_pre_commit_hook_stops_a_commit_that_adds_a_real_name(self):
        self.install()
        self.stage('a.md', 'Zorbexico\n')
        result = self.commit('add notes')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('commit STOPPED', result.stdout + result.stderr)

    def test_the_commit_msg_hook_stops_a_commit_whose_message_carries_a_real_name(self):
        self.install()
        self.stage('a.md', 'clean\n')
        result = self.commit('roll out to Zorbexico')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('COMMIT MESSAGE', result.stderr + result.stdout)

    def test_a_clean_commit_goes_through_both_hooks(self):
        self.install()
        self.stage('a.md', 'clean\n')
        self.assertEqual(self.commit('roll out to project A').returncode, 0)


class Usage(Repo):
    """The argument handling. It matters more than it looks: this script is wired
    into two git hooks, and a mode it does not recognise must FAIL LOUDLY rather
    than fall through to "nothing to check", which a hook would read as a pass."""

    def run_raw(self, *args):
        result = subprocess.run(['bash', CHECK, *args], cwd=self.root,
                                capture_output=True, text=True)
        return result.stdout + result.stderr, result.returncode

    def test_no_mode_at_all_is_a_usage_error(self):
        out, code = self.run_raw()
        self.assertIn('usage:', out)
        self.assertEqual(2, code, 'an unrecognised invocation must not look like a pass')

    def test_an_unknown_mode_is_a_usage_error(self):
        out, code = self.run_raw('--everything')
        self.assertIn('usage:', out)
        self.assertEqual(2, code)

    def test_message_mode_without_a_file_is_a_usage_error(self):
        out, code = self.run_raw('--message')
        self.assertIn('--message FILE', out)
        self.assertEqual(2, code)


if __name__ == '__main__':
    unittest.main()
