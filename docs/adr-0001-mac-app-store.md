# ADR-0001: Distributing the desktop window through the Mac App Store

**Status:** Proposed. A draft for the maintainer to decide; nothing here is built.
**Date:** 03/10/2026
**Deciders:** the maintainer (the Apple Developer Program account holder).

## Context

The desktop window (`desktop/`, Tauri) is a thin shell: it runs `python3 board_open.py --url`, which makes sure
the board server is running and prints its address, and then shows that address in a window
(`docs/live-board.md` §2g). What matters for distribution:

- **The server outlives the window, on purpose.** `board_ensure.start` launches it with `start_new_session=True`.
  The browser page, the installed web app and the window all use the same server on `127.0.0.1:8765`, and a new
  Claude Code session finds it already running.
- **The data is not the app's.** Claude Code's hooks write `<project>/.claude/board/events.jsonl` in every
  project, and the board writes `control.json` back there. None of it lives inside an app container.
- **The code is not the app's.** The shell finds `board_open.py` at `~/.claude/scripts/board` and needs a system
  `python3`. The scripts are installed separately, with Claude Code's hooks.
- **Today's path works.** A Developer ID signature plus notarization (`SETUP.md`, "Desktop window") needs no change
  to this design.

What Apple requires of Mac App Store apps (quoted from the
[App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/), read on 03/10/2026):

| Guideline | Text | What it touches here |
|---|---|---|
| 2.4.5(i) | "They must be appropriately sandboxed, and follow macOS File System Documentation." | the app could reach neither `~/.claude/scripts` nor the projects' `.claude/board` folders without user-granted access |
| 2.4.5(ii) | "...self-contained, single app installation bundles and cannot install code or resources in shared locations." | the scripts and the interpreter are outside the bundle |
| 2.4.5(iii) | "...nor spawn processes that continue to run without consent after a user has quit the app." | the detached board server |
| 2.5.2 | "Apps should be self-contained in their bundles, and may not read or write data outside the designated container area..." | the same two points |
| 4.2 | "Your app should include features, content, and UI that elevate it beyond a repackaged website." | a window around a local web page |

Tauri's own guide for the store ([App Store](https://v2.tauri.app/distribute/app-store/)) adds the mechanics: the App
Sandbox entitlement, an Apple Distribution certificate, a "Mac App Store Connect" provisioning profile, and the
entitlements file referenced from `tauri.conf.json`.

**What is not known.** How a reviewer would judge this app is not knowable from the guidelines. Whether a sandboxed
process may start the system `python3` (an Xcode command-line-tools shim) has not been tried. Both are listed below
as things to find out, not as findings.

## Decision

**None yet.** The recommendation is Option A now, and a time-boxed look at Option B only if the maintainer wants the
store badly enough to accept a redesign. Option C is rejected.

## Options considered

### Option A: Developer ID and notarization, distributed outside the store (recommended)
| Dimension | Assessment |
|---|---|
| Complexity | Low: no code change |
| Cost | The existing membership; a few hours |
| Scalability | Fine for a developer tool whose users already install Claude Code hooks |
| Fit with the design | Complete: the shell stays thin, the server stays shared |

**Pros:** works today; no sandbox, no review queue; users get no Gatekeeper warning once notarized.
**Cons:** no store listing or store updates; the app is found through the repository, not through search.

### Option B: a self-contained app for the store
The app carries its own server (the Python server with an embedded interpreter, or a Rust port), reads the boards
through folders the user grants (security-scoped bookmarks), and stops its server when it quits.
| Dimension | Assessment |
|---|---|
| Complexity | High |
| Cost | Weeks, plus review cycles with an uncertain outcome |
| Scalability | Unchanged |
| Fit with the design | Breaks two rules: "the window holds no logic" (`CLAUDE.md`) and one implementation of the server |

What it would take: a bundled interpreter or a second server implementation; a folder-grant screen for each project;
a way for the shared browser page to keep working while the app is closed (or to stop being available); sandbox
entitlements (`app-sandbox`, a network entitlement for loopback, user-selected file access); the Apple Distribution and
Mac Installer Distribution certificates, an App ID and the provisioning profile; an App Store Connect record, privacy
answers and screenshots.

**Pros:** a store listing and automatic updates.
**Cons:** the largest change in the project's history for a small audience; 4.2 is a real risk because the product is,
at heart, a page served by a local program; a second code path to keep in step with `scripts/board/`.

### Option C: a sandboxed viewer for the separately installed server
The store app only shows `http://127.0.0.1:8765` and expects the user to have installed the scripts and hooks.
**Rejected:** it depends on software the store does not deliver (2.4.5(ii), 2.5.2), a reviewer cannot test it without
that software, and it is the "repackaged website" 4.2 describes.

## Trade-off analysis

The window's value is that it is a window onto something that already runs on the machine. The store asks for the
opposite: an app that carries everything it needs and keeps to its own container. The two meet only by moving the
server inside the app, which means the board stops being a shared service that hooks, a browser and a window all use.
That is a different product, and the audience (developers who already run Claude Code and can install from a
repository) gains little from a listing.

## Consequences

- **Choosing A:** the store stays closed; distribution is a signed, notarized `.app` linked from the repository.
- **Choosing B:** `docs/live-board.md` §2g, `CLAUDE.md` (the `desktop/` exception), the gate and CI all change, and a
  decision is needed on the browser page.
- **To revisit:** if Claude Code itself ever ships a first-party channel for this kind of panel, the question changes.

## Action items

1. [ ] Decide A, or A plus a spike on B.
2. [ ] Option A: the maintainer installs the Developer ID Application certificate and stores a `notarytool` profile
   (`SETUP.md`); then sign, submit, staple and check with `spctl`.
3. [ ] If B: a one-day spike, nothing merged. Build the current shell with the sandbox entitlement and write down what
   breaks: starting `python3`, reading `~/.claude/scripts/board`, reading a project's `.claude/board`, binding the
   loopback port.
4. [ ] If B and the spike is not a dead end: a TestFlight build, whose beta review is the cheapest early signal of how
   App Review reads the guidelines above for this app.
5. [ ] If B is chosen: replace this ADR's Status with Accepted, and write the design as ADR-0002.
