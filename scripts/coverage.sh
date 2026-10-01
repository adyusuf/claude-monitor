#!/usr/bin/env bash
# Line coverage of the application (global rule #29): Python and JavaScript, each against the threshold.
# gate-core.sh runs this file as the
# coverage step when it is present (scripts/coverage.sh).
#
# Exit codes:  0  every codebase measured and at or above the threshold
#              1  the tests are red, or a measured codebase is below the threshold
#              3  something could NOT be measured — which blocks, exactly like a failure
#                 (#29: an unmeasured codebase does not count as passing)
#
# Python: `coverage` in a venv OUTSIDE the repository (COVERAGE_VENV, default
# ~/.cache/claude-monitor/venv) so no interpreter of the user's is touched. Create it once:
#     python3 -m venv ~/.cache/claude-monitor/venv && ~/.cache/claude-monitor/venv/bin/pip install coverage
# Subprocesses started by the tests are measured too (a .pth file in the venv, written
# here if missing); tests that run a COPY of a script in a temporary directory are not
# attributed to the original — measure a script through the original path.
#
# Shell: this repository authors no shell script of its own. The gate scripts under scripts/ are
# the shared copies of the configuration repository, whose own tests cover them there; this file
# does not trace them. That is a stated scope, not a waiver of product code: every file of the
# application itself is Python or JavaScript, and both are measured.
set -uo pipefail

root="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repository" >&2; exit 2; }
cd "$root" || exit 2
venv="${COVERAGE_VENV:-$HOME/.cache/claude-monitor/venv}"
min="${COVERAGE_MIN:-80}"
status=0

echo "▶ Python scripts (threshold ${min}%)"
if [ ! -x "$venv/bin/coverage" ]; then
  echo "  NOT MEASURED: no coverage in $venv"
  echo "  create it: python3 -m venv \"$venv\" && \"$venv/bin/pip\" install coverage"
  status=3
else
  purelib="$("$venv/bin/python" -c 'import sysconfig; print(sysconfig.get_paths()["purelib"])')"
  [ -f "$purelib/coverage-subprocess.pth" ] || echo 'import coverage; coverage.process_startup()' > "$purelib/coverage-subprocess.pth"
  data="$(mktemp -d)"
  # The application is scripts/board; the scripts beside it are the shared gate tools.
  export COVERAGE_SRC="$root/scripts/board" COVERAGE_DATA="$data/.coverage" COVERAGE_PROCESS_START="$root/.coveragerc"
  if ! PATH="$venv/bin:$PATH" coverage run -m unittest discover -s scripts/tests -p 'test_*.py' >"$data/tests.log" 2>&1; then
    tail -20 "$data/tests.log" | sed 's/^/  /'
    echo "  ✗ the tests are red — coverage of a red suite is not a measurement"
    status=1
  else
    PATH="$venv/bin:$PATH" coverage combine >/dev/null 2>&1
    if PATH="$venv/bin:$PATH" coverage report --fail-under="$min" | sed 's/^/  /'; then
      echo "  ✓ at or above ${min}%"
    else
      echo "  ✗ below ${min}% — the gap and the plan to close it: docs/coverage-gap.md"
      status=1
    fi
  fi
  find scripts -name __pycache__ -path '*tests*' -prune -exec rm -rf {} + 2>/dev/null
  rm -rf "$data"
fi

# JavaScript: MEASURED with Node's built-in coverage, so there is no package to
# install. The live board's page script (scripts/board/, standards/22-live-board.md) and the
# desktop window's splash page (desktop/ui/) are the JavaScript here. Node only reports files a test LOADED, so a file no test touches
# would silently leave the denominator: every file must appear in the report, or
# the codebase is NOT MEASURED (#29).
echo "▶ JavaScript (threshold ${min}%)"
js_files="$(find scripts desktop/ui -name '*.js' -not -path '*/tests/*' -not -path '*/node_modules/*' 2>/dev/null | sort)"
js_status=0
if [ -z "$js_files" ]; then
  echo "  n/a: no JavaScript here"
elif ! command -v node >/dev/null 2>&1; then
  echo "  NOT MEASURED: node is not installed"
  js_status=3
else
  js_log="$(mktemp)"
  node --test --experimental-test-coverage --test-coverage-include='scripts/**/*.js' --test-coverage-include='desktop/ui/*.js' \
       --test-coverage-exclude='scripts/tests/**' --test-coverage-lines="$min" \
       'scripts/tests/**/*.test.js' >"$js_log" 2>&1
  js_run=$?
  sed -n '/start of coverage report/,/end of coverage report/p' "$js_log" | sed 's/^/  /'
  unloaded=""
  for file in $js_files; do
    grep -q " $(basename "$file") " "$js_log" || unloaded="$unloaded $file"
  done
  if ! grep -q '^# fail 0$' "$js_log"; then
    grep -E '^not ok|^# (pass|fail) ' "$js_log" | sed 's/^/  /'
    echo "  ✗ the tests are red — coverage of a red suite is not a measurement"
    js_status=1
  elif [ -n "$unloaded" ]; then
    echo "  NOT MEASURED: no test loads$unloaded"
    js_status=3
  elif [ "$js_run" != 0 ]; then
    echo "  ✗ below ${min}%"
    js_status=1
  else
    echo "  ✓ at or above ${min}%"
  fi
  rm -f "$js_log"
fi
if [ "$js_status" != 0 ] && [ "$status" = 0 ]; then status="$js_status"; fi

# Rust: the desktop window (desktop/), a codebase of its own, measured on its own with cargo-llvm-cov
# (rustup component llvm-tools-preview; SETUP.md). Only generated code is left out: build.rs is the
# two-line Tauri build hook, whose body is generated. main.rs and lib.rs ARE counted.
echo "▶ Rust, desktop window (threshold ${min}%)"
cargo_bin="${CARGO_HOME:-$HOME/.cargo}/bin"
PATH="$cargo_bin:$PATH"
rust_status=0
if [ ! -f desktop/Cargo.toml ]; then
  echo "  n/a: no Rust here"
elif ! command -v cargo-llvm-cov >/dev/null 2>&1; then
  echo "  NOT MEASURED: cargo-llvm-cov is not installed (rustup component add llvm-tools-preview; cargo install cargo-llvm-cov --locked)"
  rust_status=3
elif ! python3 desktop/make_icon.py >/dev/null; then
  echo "  NOT MEASURED: the window icon could not be drawn (desktop/make_icon.py)"
  rust_status=3
else
  rust_log="$(mktemp)"
  if ! (cd desktop && cargo llvm-cov --ignore-filename-regex 'build\.rs$' --fail-under-lines "$min" >"$rust_log" 2>&1); then
    if grep -q '^test result: FAILED\|^error' "$rust_log"; then
      tail -25 "$rust_log" | sed 's/^/  /'
      echo "  ✗ the tests are red or the crate does not build — coverage of that is not a measurement"
    else
      grep -E '^(TOTAL|Filename)|\.rs ' "$rust_log" | sed 's/^/  /'
      echo "  ✗ below ${min}%"
    fi
    rust_status=1
  else
    grep -E '^(TOTAL|Filename)|\.rs ' "$rust_log" | sed 's/^/  /'
    echo "  ✓ at or above ${min}%"
  fi
  rm -f "$rust_log"
fi
if [ "$rust_status" != 0 ] && [ "$status" = 0 ]; then status="$rust_status"; fi

exit "$status"
