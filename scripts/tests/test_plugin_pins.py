"""scripts/write_plugin_pins.py (ADR-0007): which lines of SHA256SUMS become pins, and how the pins of one version accumulate."""
import importlib.util
import os
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location('write_plugin_pins', os.path.join(HERE, '..', 'write_plugin_pins.py'))
pins_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pins_module)


class Pins(unittest.TestCase):
    SUMS = ('a' * 64 + '  aipod-macos-arm64.zip\n' + 'B' * 64 + '  aipod-windows-x64.zip\n' + 'c' * 63 + '  aipod-linux-x64.zip\n'
            + 'd' * 64 + '  other.zip\n')

    def test_only_well_formed_build_lines_become_pins_in_a_stable_order(self):
        found = pins_module.pins(self.SUMS)
        self.assertEqual(sorted(found), ['macos-arm64', 'windows-x64'])
        text = pins_module.render('1.2.3', pins_module.merge('1.2.3', found, ''))
        self.assertEqual(text.splitlines()[0], '1.2.3')
        self.assertEqual(text.splitlines()[1], 'macos-arm64 ' + 'a' * 64 + ' aipod-macos-arm64.zip')
        self.assertEqual(text.splitlines()[2], 'windows-x64 ' + 'b' * 64 + ' aipod-windows-x64.zip')

    def test_the_builds_of_one_version_accumulate_and_a_new_version_starts_over(self):
        test_build = pins_module.pins('1' * 64 + '  aipod-linux-x64.zip\n')
        prod_build = pins_module.pins('2' * 64 + '  aipod-linux-x64.zip\n')
        first = pins_module.render('1.2.3', pins_module.merge('1.2.3', test_build, ''))
        both = pins_module.render('1.2.3', pins_module.merge('1.2.3', prod_build, first))
        self.assertEqual(both.splitlines(), ['1.2.3', 'linux-x64 ' + '1' * 64 + ' aipod-linux-x64.zip', 'linux-x64 ' + '2' * 64 + ' aipod-linux-x64.zip'])
        again = pins_module.render('1.2.3', pins_module.merge('1.2.3', prod_build, both))
        self.assertEqual(again, both)  # writing the same build twice changes nothing
        newer = pins_module.render('1.3.0', pins_module.merge('1.3.0', prod_build, both))
        self.assertEqual(newer.splitlines(), ['1.3.0', 'linux-x64 ' + '2' * 64 + ' aipod-linux-x64.zip'])
        self.assertEqual(pins_module.existing('not a version\nx y z\n'), (None, {}))

    def test_replace_starts_the_list_over_for_a_superseding_build(self):
        with tempfile.TemporaryDirectory() as downloads:
            sums = os.path.join(downloads, 'SHA256SUMS')
            open(sums, 'w').write('1' * 64 + '  aipod-linux-x64.zip\n')
            targets = [os.path.join(os.path.dirname(HERE), '..', 'plugin', name, 'agent-release.txt') for name in ('monitor-agent', 'aipod')]
            target = targets[0]
            before = [open(t).read() for t in targets]
            try:
                self.assertEqual(pins_module.main(['x', downloads, '1.2.3', '--replace']), 0)
                open(sums, 'w').write('2' * 64 + '  aipod-linux-x64.zip\n')
                self.assertEqual(pins_module.main(['x', downloads, '1.2.3']), 0)
                self.assertEqual(len(open(target).read().splitlines()), 3)  # both builds accumulated
                self.assertEqual(pins_module.main(['x', downloads, '1.2.3', '--replace']), 0)
                self.assertEqual(open(target).read().splitlines(), ['1.2.3', 'linux-x64 ' + '2' * 64 + ' aipod-linux-x64.zip'])
                self.assertEqual(open(targets[1]).read(), open(target).read())  # the aipod plugin pins the same builds
            finally:
                for t, text in zip(targets, before):
                    open(t, 'w').write(text)  # the committed files are not the test's to change

    def test_a_bad_version_or_a_missing_sums_file_is_refused(self):
        with tempfile.TemporaryDirectory() as empty:
            self.assertEqual(pins_module.main(['x', empty, '1.2']), 2)
            self.assertEqual(pins_module.main(['x', empty, '1.2.3']), 2)  # no SHA256SUMS
            with open(os.path.join(empty, 'SHA256SUMS'), 'w') as f:
                f.write('nothing useful\n')
            self.assertEqual(pins_module.main(['x', empty, '1.2.3']), 2)




if __name__ == '__main__':
    unittest.main()
