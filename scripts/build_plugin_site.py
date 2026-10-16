#!/usr/bin/env python3
"""Writes the plugin's own-server marketplace into a downloads folder (ADR-0007, section 9.2).

    python3 scripts/build_plugin_site.py <downloads-dir> --base-url https://<site>

Two files land in the folder the deploy serves at /downloads:
  monitor-agent-plugin.zip   plugin/monitor-agent, with its pins (agent-release.txt) as they are in the working tree; reproducible
  marketplace.json           a URL catalog named monitor-agent-releases (the name the plugin's wrapper expects) whose one plugin is an
                             "archive" source: <base-url>/downloads/monitor-agent-plugin.zip with its sha256, which Claude Code checks

A person then runs `claude plugin marketplace add <base-url>/downloads/marketplace.json` and `claude plugin install monitor-agent@monitor-agent-releases`.
Claude Code refuses an archive over http or on a loopback, link-local or cloud-metadata host, so this refuses them too. The site's address is a
deployment value: it is passed here, never kept in the repository. Exit 0 = written, 2 = refused.
"""
import argparse
import hashlib
import ipaddress
import json
import os
import re
import stat
import sys
import zipfile
from urllib.parse import urlsplit

NAME = 'monitor-agent-releases'
PLUGIN = 'monitor-agent'
ZIP = 'monitor-agent-plugin.zip'
FIXED_TIME = (2026, 1, 1, 0, 0, 0)  # the same files give the same zip, so the same sha256
HOST = re.compile(r'^[A-Za-z0-9.-]+$')


def refuse(message):
    print(f'refused: {message}', file=sys.stderr)
    sys.exit(2)


def check_base(url):
    parts = urlsplit(url)
    if parts.scheme != 'https':
        refuse('the site address must be https (Claude Code does not install an archive over http)')
    host = parts.hostname or ''
    if not host or not HOST.match(host) or parts.username or parts.password or parts.query or parts.fragment or parts.path not in ('', '/'):
        refuse('the site address must be https://<host>[:port] and nothing else')
    lowered = host.lower()
    if lowered == 'localhost' or lowered.endswith('.localhost') or lowered.endswith('.local'):
        refuse('Claude Code does not install an archive from a loopback host')
    try:
        ip = ipaddress.ip_address(lowered)
    except ValueError:
        return parts.scheme + '://' + parts.netloc
    if ip.is_loopback or ip.is_link_local or ip.is_unspecified or str(ip) == '169.254.169.254':
        refuse('Claude Code does not install an archive from a loopback, link-local or cloud-metadata address')
    return parts.scheme + '://' + parts.netloc


def plugin_files(folder):
    found = []
    for root, dirs, files in os.walk(folder):
        dirs[:] = sorted(d for d in dirs if d not in ('.git', '__pycache__'))
        for name in sorted(files):
            if name == '.DS_Store':
                continue
            path = os.path.join(root, name)
            found.append((os.path.relpath(path, folder).replace(os.sep, '/'), path))
    return sorted(found)


def write_zip(folder, target):
    files = plugin_files(folder)
    if not any(rel == '.claude-plugin/plugin.json' for rel, _ in files):
        refuse(f'{folder} has no .claude-plugin/plugin.json: it is not a plugin folder')
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as archive:
        for rel, path in files:
            info = zipfile.ZipInfo(rel, FIXED_TIME)
            executable = os.stat(path).st_mode & stat.S_IXUSR
            info.external_attr = ((0o755 if executable else 0o644) | stat.S_IFREG) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(path, 'rb') as handle:
                archive.writestr(info, handle.read())
    digest = hashlib.sha256()
    with open(target, 'rb') as handle:
        digest.update(handle.read())
    return digest.hexdigest()


def catalog(base, sha256, description):
    return {
        'name': NAME,
        'owner': {'name': 'Claude Monitor'},
        'description': description,
        'plugins': [{
            'name': PLUGIN,
            'source': {'source': 'archive', 'url': f'{base}/downloads/{ZIP}', 'sha256': sha256},
            'description': 'Installs the Claude Monitor agent, connects it to your server and starts it with your Claude Code sessions.',
        }],
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('downloads')
    parser.add_argument('--base-url', required=True)
    parser.add_argument('--plugin-dir', default=None)
    args = parser.parse_args(argv)
    base = check_base(args.base_url)
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    folder = args.plugin_dir or os.path.join(root, 'plugin', PLUGIN)
    if not os.path.isdir(folder):
        refuse(f'plugin folder {folder} not found')
    os.makedirs(args.downloads, exist_ok=True)
    sha256 = write_zip(folder, os.path.join(args.downloads, ZIP))
    description = 'The Claude Monitor agent as a Claude Code plugin, served by this site.'
    with open(os.path.join(args.downloads, 'marketplace.json'), 'w', encoding='utf-8') as handle:
        json.dump(catalog(base, sha256, description), handle, indent=2)
        handle.write('\n')
    print(f'wrote {ZIP} (sha256 {sha256}) and marketplace.json for {base}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
