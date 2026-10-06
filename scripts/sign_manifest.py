#!/usr/bin/env python3
"""Signs the agent builds for self-update (ADR-0004) and generates the signing keys.

  sign_manifest.py keygen --channel test|prod             make a key pair; the PRIVATE key goes to the macOS Keychain
                                                          only, the public key is printed (commit it as
                                                          deploy/update-keys/<channel>.pub)
  sign_manifest.py sign --channel test|prod --downloads DIR --version X.Y.Z --min-supported X.Y.Z
                                                          write DIR/manifest.json: one signed entry per cm-agent-<os>-<arch>.zip

The private key is never written to a file and never put on a command line: it is read from the Keychain (or, off a Mac,
from the environment variable named by --key-env) and handed to openssl on standard input. ECDSA P-256 / SHA-256, DER
signatures, public key as base64 SubjectPublicKeyInfo: exactly what ClaudeMonitor.Contracts.UpdateManifest verifies.
The signed text is built by payload() below and by UpdateManifest.Payload; a known-answer test in both test suites keeps them equal.
"""
import argparse
import base64
import getpass
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile

FORMAT = 'cm-agent-update/1'
CHANNELS = ('test', 'prod')
ZIP = re.compile(r'^cm-agent-(macos|windows|linux)-(arm64|x64)\.zip$')
VERSION = re.compile(r'^\d+\.\d+\.\d+$')
# The committed public halves (deploy/update-keys/<channel>.pub); tests point this at a temporary folder.
KEYS = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'deploy', 'update-keys')


def service(channel):
    return f'cm-agent-update-{channel}'


def payload(channel, version, os_name, arch, sha256, min_supported):
    for value in (channel, version, os_name, arch, sha256, min_supported):
        if not value or '\n' in value or '\r' in value:
            raise ValueError('a manifest field is empty or holds a line break')
    return (f'{FORMAT}\nchannel={channel}\nversion={version}\nos={os_name}\narch={arch}\n'
            f'sha256={sha256}\nminSupported={min_supported}\n')


def pem(key_b64):
    body = '\n'.join(key_b64[i:i + 64] for i in range(0, len(key_b64), 64))
    return f'-----BEGIN EC PRIVATE KEY-----\n{body}\n-----END EC PRIVATE KEY-----\n'


def openssl(args, data=None):
    result = subprocess.run(['openssl', *args], input=data, capture_output=True, check=False)
    if result.returncode != 0:
        raise RuntimeError('openssl ' + args[0] + ' failed: ' + result.stderr.decode(errors='replace').strip())
    return result.stdout


def read_private(channel, key_env=None):
    """The private key as base64 SEC1 DER: from the environment variable when named, else from the Keychain."""
    if key_env:
        value = os.environ.get(key_env, '').strip()
        if not value:
            raise RuntimeError(f'{key_env} is not set')
        return value
    result = subprocess.run(['security', 'find-generic-password', '-s', service(channel), '-a', getpass.getuser(), '-w'],
                            capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise RuntimeError(f'no signing key for channel {channel} in the Keychain (run keygen first)')
    return result.stdout.strip()


def public_key(private_b64):
    der = openssl(['ec', '-pubout', '-outform', 'DER', '-in', '/dev/stdin'], pem(private_b64).encode())
    return base64.b64encode(der).decode()


def sign(private_b64, text):
    """Base64 DER ECDSA signature of text. The key travels on stdin, the (public) text in a temporary file."""
    with tempfile.NamedTemporaryFile('w', suffix='.txt') as handle:
        handle.write(text)
        handle.flush()
        return base64.b64encode(openssl(['dgst', '-sha256', '-sign', '/dev/stdin', handle.name], pem(private_b64).encode())).decode()


def verify(public_b64, text, signature_b64):
    """True when openssl itself verifies the signature under the public key (the same check an agent makes)."""
    with tempfile.TemporaryDirectory() as folder:
        paths = {name: os.path.join(folder, name) for name in ('key', 'sig', 'msg')}
        for name, mode, data in (('key', 'wb', base64.b64decode(public_b64)), ('sig', 'wb', base64.b64decode(signature_b64)), ('msg', 'w', text)):
            with open(paths[name], mode) as f:
                f.write(data)
        done = subprocess.run(['openssl', 'dgst', '-sha256', '-verify', paths['key'], '-keyform', 'DER', '-signature', paths['sig'], paths['msg']],
                              capture_output=True, check=False)
        return done.returncode == 0


def keygen(channel):
    if subprocess.run(['security', 'find-generic-password', '-s', service(channel), '-a', getpass.getuser()],
                      capture_output=True, check=False).returncode == 0:
        raise RuntimeError(f'a signing key for {channel} already exists in the Keychain; it is never overwritten '
                           '(losing it strands every installed agent): delete it by hand if you really mean to rotate')
    raw = openssl(['ecparam', '-name', 'prime256v1', '-genkey', '-noout', '-outform', 'DER'])
    private_b64 = base64.b64encode(raw).decode()
    # `security -i` reads the command from standard input, so the key is never on a command line.
    added = subprocess.run(['security', '-i'], input=f'add-generic-password -s {service(channel)} -a {getpass.getuser()} -w {private_b64}\n',
                           capture_output=True, text=True, check=False)
    if added.returncode != 0:
        raise RuntimeError('the Keychain refused the key: ' + added.stderr.strip())
    return public_key(private_b64)


def build_manifest(channel, downloads, version, min_supported, private_b64):
    if channel not in CHANNELS:
        raise ValueError(f'channel must be one of {CHANNELS}')
    if not VERSION.match(version) or not VERSION.match(min_supported):
        raise ValueError('versions are x.y.z')
    entries = []
    for name in sorted(os.listdir(downloads)):
        match = ZIP.match(name)
        if not match:
            continue
        with open(os.path.join(downloads, name), 'rb') as f:
            digest = hashlib.sha256(f.read()).hexdigest()
        text = payload(channel, version, match.group(1), match.group(2), digest, min_supported)
        signature = sign(private_b64, text)
        if not verify(public_key(private_b64), text, signature):
            raise RuntimeError(f'the signature of {name} does not verify under its own key: not publishing')
        entries.append({'channel': channel, 'version': version, 'os': match.group(1), 'arch': match.group(2), 'file': name,
                        'sha256': digest, 'minSupported': min_supported, 'signature': signature})
    if not entries:
        raise RuntimeError(f'no cm-agent-<os>-<arch>.zip in {downloads}')
    return {'format': FORMAT, 'entries': entries}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    k = sub.add_parser('keygen')
    k.add_argument('--channel', required=True, choices=CHANNELS)
    s = sub.add_parser('sign')
    s.add_argument('--channel', required=True, choices=CHANNELS)
    s.add_argument('--downloads', required=True)
    s.add_argument('--version', required=True)
    s.add_argument('--min-supported', required=True)
    s.add_argument('--key-env', help='read the private key (base64 SEC1 DER) from this environment variable instead of the Keychain')
    args = parser.parse_args(argv)
    try:
        if args.command == 'keygen':
            print(keygen(args.channel))
            return 0
        private_b64 = read_private(args.channel, args.key_env)
        shipped = os.path.join(KEYS, args.channel + '.pub')
        if os.path.exists(shipped):
            with open(shipped, encoding='utf-8') as f:
                if f.read().strip() != public_key(private_b64):
                    raise RuntimeError(f'the signing key is not the one in deploy/update-keys/{args.channel}.pub: agents built with that file would refuse every update')
        manifest = build_manifest(args.channel, args.downloads, args.version, args.min_supported, private_b64)
        target = os.path.join(args.downloads, 'manifest.json')
        with open(target, 'w', encoding='utf-8') as f:
            json.dump(manifest, f, indent=2)
            f.write('\n')
        print(f'signed {len(manifest["entries"])} builds ({args.channel} {args.version}) -> {target}')
        return 0
    except (RuntimeError, ValueError) as e:
        print(f'sign_manifest: {e}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
