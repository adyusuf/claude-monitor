#!/usr/bin/env python3
"""Writes the plugin's own-server marketplace into a downloads folder (ADR-0007, section 9.2).

    python3 scripts/build_plugin_site.py <downloads-dir> --base-url https://<site>

Four files land in the folder the deploy serves at /downloads (ADR-0013 section 1: both plugins, side by side, from the same agent builds):
  aipod-plugin.zip           plugin/aipod, with its pins (agent-release.txt) as they are in the working tree; reproducible
  aipod-marketplace.json     a URL catalog named aipod-releases (the name the plugin's wrapper expects) whose one plugin is an
                             "archive" source: <base-url>/downloads/aipod-plugin.zip with its sha256, which Claude Code checks
  monitor-agent-plugin.zip   plugin/monitor-agent (the bridge to aipod), the same way
  marketplace.json           its catalog, named monitor-agent-releases: the file the machines already on the old plugin follow

A person then runs `claude plugin marketplace add <base-url>/downloads/aipod-marketplace.json` and `claude plugin install aipod@aipod-releases`.
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

# (plugin folder and name, catalog name, zip, catalog file): the old plugin keeps the names it has had, so machines that follow its catalog keep working.
PLUGINS = [
    ('aipod', 'aipod-releases', 'aipod-plugin.zip', 'aipod-marketplace.json'),
    ('monitor-agent', 'monitor-agent-releases', 'monitor-agent-plugin.zip', 'marketplace.json'),
]
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


def catalog(base, sha256, spec):
    plugin, name, zip_name, _ = spec
    return {
        'name': name,
        'owner': {'name': 'aipod' if plugin == 'aipod' else 'Claude Monitor'},
        'description': 'The aipod agent as a Claude Code plugin, served by this site.' if plugin == 'aipod'
        else 'The old plugin of the aipod agent (a bridge: install the aipod plugin instead), served by this site.',
        'plugins': [{
            'name': plugin,
            'source': {'source': 'archive', 'url': f'{base}/downloads/{zip_name}', 'sha256': sha256},
            'description': 'Installs the aipod agent, connects it to your server and starts it with your Claude Code sessions.' if plugin == 'aipod'
            else 'Replaced by the aipod plugin: install that one and run /aipod:connect.',
        }],
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('downloads')
    parser.add_argument('--base-url', required=True)
    parser.add_argument('--plugin-dir', default=None, help='build only the old plugin (monitor-agent) from this folder')
    parser.add_argument('--aipod-dir', default=None, help='build the aipod plugin from this folder (with --plugin-dir: both)')
    args = parser.parse_args(argv)
    base = check_base(args.base_url)
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    given = {'monitor-agent': args.plugin_dir, 'aipod': args.aipod_dir}
    only_given = args.plugin_dir is not None or args.aipod_dir is not None
    jobs = [(spec, given[spec[0]] or os.path.join(root, 'plugin', spec[0])) for spec in PLUGINS if not only_given or given[spec[0]]]
    for _, folder in jobs:
        if not os.path.isdir(folder):
            refuse(f'plugin folder {folder} not found')
    os.makedirs(args.downloads, exist_ok=True)
    for spec, folder in jobs:
        sha256 = write_zip(folder, os.path.join(args.downloads, spec[2]))
        with open(os.path.join(args.downloads, spec[3]), 'w', encoding='utf-8') as handle:
            json.dump(catalog(base, sha256, spec), handle, indent=2)
            handle.write('\n')
        print(f'wrote {spec[2]} (sha256 {sha256}) and {spec[3]} for {base}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
