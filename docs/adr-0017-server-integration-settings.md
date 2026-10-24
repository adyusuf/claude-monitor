# ADR-0017: Integration settings in one place

**Status:** Decided by the maintainer (24/10/2026).
**Extends:** [ADR-0015](adr-0015-work-tracker-integrations.md) §1 row 2, which set the Jira app in the environment (`AIPOD_JIRA_CLIENT_ID` / `_SECRET`). The environment still works; the page is an addition.
**Uses:** the `SecretBox` and `AIPOD_INTEGRATION_KEY` of [ADR-0015](adr-0015-work-tracker-integrations.md) §5, and the `AIPOD_*` names of [ADR-0016](adr-0016-podium-aipod-naming.md).

## 1. Problem

SMTP, the GitHub and Google sign-in apps and the Jira app were set only in the server's `aipod.env`. Changing one meant editing that file on the server and redeploying. Nothing showed which values were missing, or which callback address to register at each provider.

## 2. Decisions (maintainer, 24/10/2026)

| # | Decision |
|---|---|
| 1 | **Settings → Integrations**, a server-wide page at `/server/integrations`. API: `GET /api/server/integrations`, `PUT /api/server/integrations/{id}`, `POST /api/server/integrations/smtp/test`. |
| 2 | **One card per integration:** SMTP, GitHub sign-in, Google sign-in, Jira. Each shows its status (ready, incomplete or off), what is missing, the callback address to register at the provider, fields for the missing values, and for SMTP a test-mail button. The callback addresses are `https://<host>/api/auth/callback/github`, `https://<host>/api/auth/callback/google` and `https://<host>/api/integrations/jira/callback`. |
| 3 | **Who:** the owners and admins of ONE designated admin workspace, `AIPOD_ADMIN_WORKSPACE=<workspace id>` in `aipod.env`. Unset means nobody (fail-closed). Everyone else gets 404. `/api/me` gains `serverAdmin` (additive). |
| 4 | **Storage:** table `server_settings` ([data-model §9](data-model.md)). Secrets are sealed with `AIPOD_INTEGRATION_KEY` (AES-256-GCM, the same `SecretBox` as ADR-0015's tokens). Without the key the page is read-only. |
| 5 | **Secrets never leave the API:** never sent back to the web, never logged, never in an audit row. The audit row `server.settings_changed` names the changed fields only. |
| 6 | **Precedence:** a non-empty environment value always wins. For SMTP the `Smtp` node of a server-only appsettings counts the same way. A value that wins is shown as "set on the server (VAR)" and cannot be changed on the page. |
| 7 | **Saving** needs a recent sign-in or the two-step code, the same rule as connecting Jira. |
| 8 | **No restart:** the API keeps the settings in force in memory. It reads them at start, once a minute, and right after a save. |
| 9 | **Always registered:** the GitHub and Google sign-in schemes are always registered, with placeholders when not set. A provider is offered only when its app is set. The Jira background services always run and idle without an app. |
| 10 | **Startup check unchanged:** SMTP host, port and sender stay required in the environment outside development, so there they always come from it. Every other field (the SMTP user name, app password and STARTTLS, and the GitHub, Google and Jira client ids and secrets) can be filled on the page. An empty value removes the saved one, and the environment's value or nothing applies again. |

## 3. Who may manage it

The admin workspace is one designated workspace, not "any workspace admin". Anyone who signs up can create a workspace and become its owner. Sign-in and mail are server-wide: a stranger who could save a GitHub or Google sign-in app of their own could replace the one the server uses and take over accounts. The owners and admins of the admin workspace therefore manage the server's sign-in and mail, and nobody else can reach the page.

## 4. Storage and secrets

The database backup now also holds the sealed integration secrets. They are readable only with `AIPOD_INTEGRATION_KEY`, so that key must be kept in the maintainer's password manager, as `AIPOD_MFA_KEY` is. If the key is lost, the secrets are entered again on the page, and Jira accounts are linked again: the Atlassian tokens are sealed with the same key (ADR-0015 §5).

## 5. Precedence and existing deployments

A server that sets everything in `aipod.env` behaves exactly as before: nothing on the page overrides it and the startup check is the same. The page fills only what the environment leaves empty. The reason for this order, as the maintainer gave it, is that existing deployments must not change behaviour.

## 6. Taking effect without a restart

The redeploy was the cost named in §1. The API therefore re-reads the settings at start, once a minute and right after a save, so a saved value takes effect without a restart.

## 7. Consequences

- The Jira app can be set on the page as well as in the environment (ADR-0015 §1 row 2). Its callback address is shown on the card.
- A change to sign-in or mail is guarded by a recent sign-in or a code, because it affects every user.
- An environment value can only be changed in the file, followed by a redeploy (deploy.ps1 writes it into web.config).
- The audit trail records which fields changed, never their values.
- Backup and key handling are as in §4.

## 8. Personal data

`server_settings` holds server configuration, not anyone's data: the account export and account deletion leave it alone. Its `updated_by` keeps the user's id, which stays valid because users are never removed, only emptied (see [data-model](data-model.md) "Your data").
