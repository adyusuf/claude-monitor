# shellcheck shell=bash
# gate-core.sh's settings source, split out so that no file passes 300 lines (rule #9). A TWIN: copy it with the other
# four (scripts/twins.txt). Sourced by gate-core.sh; it defines functions only.
#
# Where every panel value comes from (user decision 06/11/2026, global #34):
#   1. the LIVE panel, when the podium plugin is installed and enabled on this machine and its settings tool is there:
#      `sdlc_settings.py show` (the shipped defaults < the user's file < this session's layer, CLAUDE_CODE_SESSION_ID);
#   2. otherwise the copy the panel wrote into the project, .claude/sdlc-settings.json (CI, a server, a machine without
#      the plugin).
# PODIUM_SETTINGS_SOURCE=file forces 2 (the tests use it so a developer's own panel cannot change their result), and so
# does CI (CI or GITHUB_ACTIONS set): a self-hosted runner runs as the user, so it would otherwise read the live panel
# instead of what the project committed (qa 06/11/2026).
gate_in_ci() { [ -n "${CI:-}" ] || [ -n "${GITHUB_ACTIONS:-}" ]; }
# The chosen JSON lands in one file and SDLC_SETTINGS_FILE points at it, so every reader stays one `open()`;
# SDLC_SETTINGS_LABEL is what the messages name. A live panel that cannot be read falls back to 2 with a warning.

SDLC_SETTINGS_LABEL="${SDLC_SETTINGS_FILE:-.claude/sdlc-settings.json}"

# The path of the enabled podium plugin's settings tool, or nothing. Enabled = `enabledPlugins["podium@…"]` is true in
# the user's settings; installed = installed_plugins.json names an install path that holds scripts/sdlc_settings.py.
podium_live_tool() {
  have python3 || return 1
  python3 -c 'import json, os, sys
home = os.environ.get("CLAUDE_CONFIG_DIR") or os.path.join(os.path.expanduser("~"), ".claude")
try:
    enabled = json.load(open(os.path.join(home, "settings.json"), encoding="utf-8")).get("enabledPlugins") or {}
    installed = json.load(open(os.path.join(home, "plugins", "installed_plugins.json"), encoding="utf-8")).get("plugins") or {}
except Exception:
    sys.exit(1)
for key, on in sorted(enabled.items()):
    if on is not True or key.split("@")[0] != "podium":
        continue
    for record in installed.get(key) or []:
        tool = os.path.join(str(record.get("installPath") or ""), "scripts", "sdlc_settings.py")
        if os.path.isfile(tool):
            print(tool); sys.exit(0)
sys.exit(1)' 2>/dev/null
}

PANEL_RESOLVED=0
panel_resolve() {
  [ "$PANEL_RESOLVED" = 0 ] || return 0
  PANEL_RESOLVED=1
  SDLC_SETTINGS_LABEL="$SDLC_SETTINGS_FILE"
  [ "${PODIUM_SETTINGS_SOURCE:-auto}" = "file" ] && return 0
  gate_in_ci && return 0
  local tool live
  tool="$(podium_live_tool)" || return 0
  live="$(mktemp "${TMPDIR:-/tmp}/podium-panel.XXXXXX")" || return 0
  if python3 "$tool" show ${CLAUDE_CODE_SESSION_ID:+--session "$CLAUDE_CODE_SESSION_ID"} > "$live" 2>/dev/null \
     && python3 -c 'import json, sys; json.load(open(sys.argv[1], encoding="utf-8"))["settings"]' "$live" 2>/dev/null; then
    SDLC_SETTINGS_FILE="$live"
    # shellcheck disable=SC2034  # removed by gate_result in gate-lib.sh
    PANEL_LIVE_FILE="$live"
    # shellcheck disable=SC2034  # read by the messages in gate-lib.sh and gate-lib-prod.sh
    SDLC_SETTINGS_LABEL="the podium panel (live)"
    printf '  settings: the podium panel (live, %s)\n' "$tool"
  else
    rm -f "$live"
    warn "the podium plugin is enabled but its panel could not be read - using $SDLC_SETTINGS_FILE"
  fi
}

# panel_value <dotted.key> -> yes | no | unset | unreadable (only an explicit true/false is yes/no).
panel_value() {
  panel_resolve
  [ -f "$SDLC_SETTINGS_FILE" ] || { echo "unset"; return 0; }
  have python3 || { echo "unreadable"; return 0; }
  python3 -c 'import json, sys
try:
    node = json.load(open(sys.argv[1], encoding="utf-8"))["settings"]
    for part in sys.argv[2].split("."):
        node = node[part]
except (KeyError, TypeError):
    print("unset"); sys.exit(0)
except Exception:
    print("unreadable"); sys.exit(0)
print("yes" if node is True else "no" if node is False else "unset")' "$SDLC_SETTINGS_FILE" "$1" 2>/dev/null || echo "unreadable"
}
