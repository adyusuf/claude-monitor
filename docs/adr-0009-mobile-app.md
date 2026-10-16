# ADR-0009: A mobile app for Claude Monitor

**Status:** Planning only (16/10/2026). Nothing in this document is decided, built or approved; it lists what the app would need, what
it would change in the API, and the questions the maintainer must answer first. The maintainer deferred the app until after the
machine phases (ADR-0008 §9.18).
**Extends:** [ADR-0008](adr-0008-machine-awareness-script-runs-and-cross-machine-help.md) §3h (the room the machine phases leave for it),
[ADR-0002](adr-0002-agent-platform.md) (one API for every client).
**Asked for (16/10/2026):** a mobile app that talks to the same API, approves and triggers work, and follows sessions and machines.

## 1. What the app would do (first version, proposed)

| Need | API it reads or calls today | Gap |
|---|---|---|
| Sign in (e-mail and password, GitHub, Google, TOTP) | `/api/auth/*`, `/api/auth/external/*`, `/api/auth/mfa/*` | the session is an HttpOnly cookie and the external sign-in ends in browser redirects (§2) |
| Follow sessions and machines | `/api/sessions`, `/api/machines`, the stream endpoints | none known; the stream is SSE (§5) |
| Approve or deny a run, with the code on strict targets | `/api/runs/{id}/approve`, `/deny` | the app must be told a run waits (push, §3) |
| Trigger an approved test job and read its result | `/api/jobs`, `/api/runs`, `/api/runs/{id}/output` | none known |
| Send a prompt into a session | `/api/sessions/{id}/commands` | none known |
| View and cancel asks | `/api/asks` | none known |

Every endpoint above already returns data and stable error strings (ADR-0008 §3h), so no mobile-only endpoint is planned (global #5).

## 2. Sign-in

Today a browser holds an HttpOnly cookie; a native app cannot drive the redirect flows or read that cookie the same way.

| Option | Notes |
|---|---|
| A. Authorization code with PKCE against the existing external providers, then the API issues a token for the app | one flow for the app; the API gains a token type next to the cookie |
| B. The API issues a short-lived bearer token for any successful login (password, external, MFA), for web and mobile alike | one mechanism for every client; the web would have to change to benefit |
| C. The app opens the system browser for the whole sign-in and receives a one-time code on a registered link | no credentials typed in the app; needs a registered URL scheme or associated domain |

Questions: which option; how long the token lives and how it is refreshed; where the app keeps it (the device's secure store); how
it is revoked when a device is lost (a device list in Settings, like agent tokens).

## 3. Push notifications

The reason for the app: "a run waits for your approval" must reach the phone without opening it.

- A **device-token registration endpoint** (additive): the app registers a platform token for the signed-in user; the person can list and remove devices.
- A **sender** in the API that calls the platform push service when a run is `pending_approval`, an ask is addressed to the person, or a machine goes offline. That is a **new dependency and a new secret** (global #10, #16): a platform credential per environment, in `SETUP.md`'s inventory with its owner and rotation.
- What a notification carries: the kind and the host label only. No command, no output and no question text leaves the server in a push (captured content is sensitive; the app fetches the detail after sign-in).

Questions: which push service; whether a notification may name the machine; quiet hours per user.

## 4. Where it lives and what it uses

| Question | Options known |
|---|---|
| Stack | the global default is React Native with Expo; this repository's rule lists neither, so adding them is asked for (global #10): why, the alternative (a progressive web app on the existing React code, no store), the maintenance cost |
| Location | this repository (a `mobile/` folder next to `web/`) or its own repository; the shared contract types would be generated from the API in either case |
| End-to-end tests | Maestro is the global default; optional like every e2e (global #33) |
| Coverage | its own 80 % line gate, measured separately from web and agent (global #29); the gate script gains a step for it |
| Distribution | store accounts, signing and review are the maintainer's; an internal test channel first |
| Texts | i18n keys in `en` and `tr`, same rule as the web |

## 5. API changes this plan needs (all additive)

1. A token issuance path for native clients (§2), next to the cookie; every endpoint accepts either.
2. A device registry: register, list, remove; removed on sign-out and on account deletion (retention rules in `docs/security.md`).
3. Push events written when a run, an ask or a machine changes state, with a per-user preference.
4. A way for the app to follow changes while it is open. SSE works over the same origin; whether the platform's networking keeps it alive in the background is not known, and push covers the background case.

Nothing is renamed or removed (global #4).

## 6. Security

- The app is another client: every call is checked for membership and ownership on the server; the app's own checks are UX only (global #6).
- The strict-target code (ADR-0008 §3c) is typed on the phone, a separate device from the one where the work runs, which is the property that code exists for.
- Text from the web reaches a session through the existing wrapper; the app adds no new path into a session.
- Screenshots and the task switcher: whether the app hides captured content there is an open question.
- Biometric unlock of the stored token: open.

## 7. Questions for the maintainer

1. Native app or a progressive web app on the existing code? (The answer decides everything in §4.)
2. Sign-in: A, B or C (§2)?
3. Push service and who owns its credentials (§3)?
4. This repository or a new one (§4)?
5. Which of the first-version needs are required for a first release?
6. Do approvals from a notification need the app to be open and unlocked, or may a notification action approve directly? (The second makes a lost phone a risk; the proposal is the first.)

## 8. Not decided, not built

Everything above. No code, dependency, secret or API change is made by this document. When the maintainer answers §7 this becomes a
real ADR with a phase table, a gate step and a test plan.
