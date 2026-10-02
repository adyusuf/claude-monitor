"""scripts/mutation_check.py: the change operators, the killed/survivor verdict, and the refusals.

The checker runs real `unittest` processes over a throw-away copy, so most tests here build a tiny module
and a tiny suite in a temporary `scripts/` tree - slow enough to keep to a handful of runs.
"""
import ast
import io
import sys
import tempfile
import textwrap
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import mutation_check as mc  # noqa: E402

SOURCE = textwrap.dedent('''
    def adult(age):
        return age >= 18

    def both(a, b):
        return a and b

    def flag():
        return True

    def invert(x):
        return not x

    def small():
        return 5
''')
# Checks everything except the boundary (18) and the number 5's exact value.
SUITE = textwrap.dedent('''
    import sys, unittest
    from pathlib import Path
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
    import calc

    class T(unittest.TestCase):
        def test_adult(self): self.assertTrue(calc.adult(30)); self.assertFalse(calc.adult(3))
        def test_both(self): self.assertTrue(calc.both(1, 1)); self.assertFalse(calc.both(1, 0)); self.assertFalse(calc.both(0, 1))
        def test_flag(self): self.assertIs(calc.flag(), True)
        def test_invert(self): self.assertFalse(calc.invert(1)); self.assertTrue(calc.invert(0))
        def test_small(self): self.assertTrue(calc.small() > 0)
''')


def project(tmp: str, source: str = SOURCE, suite: str = SUITE) -> Path:
    scripts = Path(tmp) / "scripts"
    (scripts / "tests").mkdir(parents=True)
    (scripts / "calc.py").write_text(source)
    (scripts / "tests" / "test_calc.py").write_text(suite)
    return scripts / "calc.py"


class Operators(unittest.TestCase):
    def test_each_kind_of_change(self):
        # sites in source order: >= , 18 , and , True , not , 5
        expect = {0: ("age < 18", "GtE -> Lt"), 1: ("age >= 19", "18 -> 19"), 2: ("a or b", "And -> Or"),
                  3: ("return False", "True -> False"), 5: ("return 6", "5 -> 6")}
        self.assertEqual(mc.count(SOURCE), 6)
        for index, (fragment, what) in expect.items():
            changed, line, label = mc.mutate(SOURCE, index)
            self.assertIn(fragment, changed)
            self.assertEqual(label, what)
            ast.parse(changed)                      # still valid Python
            self.assertGreater(line, 0)

    def test_dropping_a_not(self):
        changed, _, what = mc.mutate(SOURCE, 4)
        self.assertEqual(what, "`not` removed")
        self.assertIn("return x", changed)
        self.assertNotIn("not x", changed)

    def test_every_comparison_has_an_opposite(self):
        for op, other in mc.COMPARE.items():
            self.assertIs(mc.COMPARE[other], op)

    def test_a_module_with_nothing_to_change(self):
        self.assertEqual(mc.count("x = 'text'\n"), 0)


class Verdict(unittest.TestCase):
    def test_caught_changes_and_survivors_are_told_apart(self):
        with tempfile.TemporaryDirectory() as tmp:
            target = project(tmp)
            result = mc.check(target, ["test_calc"], None, 0)
            self.assertEqual(result["total"], 6)
            survived = {(s["line"], s["change"]) for s in result["survivors"]}
            self.assertIn((3, "18 -> 19"), survived)       # the boundary is not tested
            self.assertIn((15, "5 -> 6"), survived)        # only "> 0" is checked
            self.assertEqual(result["killed"], 4)
            self.assertEqual(target.read_text(), SOURCE)   # the original file was never touched

    def test_a_limit_samples_the_same_changes_for_the_same_seed(self):
        with tempfile.TemporaryDirectory() as tmp:
            target = project(tmp)
            first = mc.check(target, ["test_calc"], 3, 7)
            second = mc.check(target, ["test_calc"], 3, 7)
            self.assertEqual(first["total"], 3)
            self.assertEqual(first, second)

    def test_a_red_suite_is_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            target = project(tmp, suite=SUITE.replace("calc.adult(30)", "calc.adult(3)"))
            with self.assertRaises(SystemExit) as caught:
                mc.check(target, ["test_calc"], None, 0)
            self.assertIn("not green", str(caught.exception))

    def test_a_hang_counts_as_caught(self):
        with tempfile.TemporaryDirectory() as tmp:
            target = project(tmp)
            hang = SUITE.replace("def test_flag", "def test_hang(self):\n            import time\n            time.sleep(30)\n        def test_flag")
            (target.parent / "tests" / "test_calc.py").write_text(hang)
            self.assertFalse(mc.run_tests(Path(tmp), ["test_calc"], timeout=2))


class CommandLine(unittest.TestCase):
    def run_main(self, *argv, runner=mc.run_tests):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = mc.main(list(argv), runner=runner)
        return code, out.getvalue(), err.getvalue()

    def test_the_report_names_the_survivors_and_the_score(self):
        with tempfile.TemporaryDirectory() as tmp:
            target = project(tmp)
            code, out, _ = self.run_main(str(target), "--tests", "test_calc")
            self.assertEqual(code, 0)
            self.assertIn("SURVIVOR", out)
            self.assertIn("18 -> 19", out)
            self.assertIn("4/6 changes caught (67%); 2 survivor(s)", out)

    def test_a_minimum_score_turns_survivors_into_a_failure(self):
        with tempfile.TemporaryDirectory() as tmp:
            target = project(tmp)
            self.assertEqual(self.run_main(str(target), "--tests", "test_calc", "--min-score", "0.9")[0], 1)
            self.assertEqual(self.run_main(str(target), "--tests", "test_calc", "--min-score", "0.5")[0], 0)

    def test_refusals_exit_two(self):
        self.assertEqual(self.run_main("/no/such/file.py", "--tests", "x")[0], 2)
        with tempfile.TemporaryDirectory() as tmp:
            outside = Path(tmp) / "plain.py"
            outside.write_text("x = 1 == 1\n")
            code, _, err = self.run_main(str(outside), "--tests", "x")
            self.assertEqual(code, 2)
            self.assertIn("scripts/", err)
            target = project(tmp + "/p", suite=SUITE.replace("calc.adult(30)", "calc.adult(3)"))
            code, _, err = self.run_main(str(target), "--tests", "test_calc")
            self.assertEqual(code, 2)
            self.assertIn("not green", err)

    def test_an_unchanged_module_scores_full(self):
        with tempfile.TemporaryDirectory() as tmp:
            target = project(tmp, source="x = 'text'\n", suite="import unittest\nclass T(unittest.TestCase):\n    def test_a(self): pass\n")
            code, out, _ = self.run_main(str(target), "--tests", "test_calc")
            self.assertEqual(code, 0)
            self.assertIn("0/0", out)


if __name__ == "__main__":
    unittest.main()
