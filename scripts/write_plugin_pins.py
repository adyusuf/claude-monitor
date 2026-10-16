#!/usr/bin/env python3
"""Writes plugin/monitor-agent/agent-release.txt from a build's SHA256SUMS (ADR-0007).

    python3 scripts/write_plugin_pins.py <downloads-dir> <version>

The first line of the file is the agent version; each following line is "<os>-<arch> <sha256> <zip file>". The plugin's bootstrap
(scripts/ensure.sh) downloads exactly that zip and refuses anything whose SHA-256 differs, so the server cannot substitute a build.
Run it after scripts/build-agent.sh and commit the file with the release. Exit 0 = written, 2 = refused (a missing or unreadable input).
"""
import os
import re
import sys

NAME = re.compile(r'^cm-agent-(macos|windows|linux)-(arm64|x64)\.zip$')
HEX = re.compile(r'^[0-9a-f]{64}$')
VERSION = re.compile(r'^\d+\.\d+\.\d+$')


def pins(sums_text):
    found = {}
    for line in sums_text.splitlines():
        parts = line.split()
        if len(parts) != 2:
            continue
        digest, name = parts[0].lower(), parts[1].lstrip('*')
        match = NAME.match(os.path.basename(name))
        if match and HEX.match(digest):
            found[f'{match.group(1)}-{match.group(2)}'] = (digest, os.path.basename(name))
    return found


def render(version, found):
    return version + '\n' + ''.join(f'{key} {digest} {name}\n' for key, (digest, name) in sorted(found.items()))


def main(argv):
    if len(argv) != 3 or not VERSION.match(argv[2]):
        print(__doc__.strip().splitlines()[0] + '\nusage: write_plugin_pins.py <downloads-dir> <x.y.z>', file=sys.stderr)
        return 2
    sums = os.path.join(argv[1], 'SHA256SUMS')
    try:
        found = pins(open(sums, encoding='utf-8').read())
    except OSError as error:
        print(f'cannot read {sums}: {error}', file=sys.stderr)
        return 2
    if not found:
        print(f'no cm-agent zip in {sums}', file=sys.stderr)
        return 2
    target = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'plugin', 'monitor-agent', 'agent-release.txt')
    with open(target, 'w', encoding='utf-8', newline='\n') as handle:
        handle.write(render(argv[2], found))
    print(f'{len(found)} pins for version {argv[2]} written to {target}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
