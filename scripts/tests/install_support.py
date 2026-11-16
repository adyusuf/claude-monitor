"""The installers as the download host serves them (F-332): each one file, joined from the step files by scripts/install/assemble.sh.

The installer tests run these assembled files, never the parts, because the assembled file is what a user downloads. The assembly happens
once per test process, into a private folder that is removed when the process ends.
"""
import atexit
import os
import shutil
import subprocess
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
INSTALL_DIR = os.path.normpath(os.path.join(HERE, '..', 'install'))
ASSEMBLE = os.path.join(INSTALL_DIR, 'assemble.sh')
SH, PS1 = 'aipod-install.sh', 'aipod-install.ps1'
_folder = None


def assemble(out):
    """Runs assemble.sh into `out` and returns the finished process (no check: a test may expect it to fail)."""
    return subprocess.run(['bash', ASSEMBLE, out], capture_output=True, text=True)


def assembled_dir():
    """The folder holding the assembled aipod-install.sh and aipod-install.ps1, built on first use."""
    global _folder
    if _folder is None:
        folder = tempfile.mkdtemp(prefix='aipod-installers-')
        atexit.register(shutil.rmtree, folder, True)
        result = assemble(folder)
        if result.returncode != 0:
            raise RuntimeError(f'assemble.sh failed: {result.stdout}{result.stderr}')
        _folder = folder
    return _folder


def assembled(name):
    """The path of one assembled installer (SH or PS1)."""
    return os.path.join(assembled_dir(), name)
