# ADR-0018: The look of the web app

**Status:** Decided by the maintainer (04/11/2026). Done on dev (`b7b9ae4`, `11f3cbc`, and the follow-ups of this record).
**Scope:** the React app in `web/` only. The tray app and the agent have no web look to keep.
**Rule it adds:** the **Web look** line of `CLAUDE.md`.

## 1. Problem

The web app had grown page by page: grey cards on a grey ground, one shadow everywhere, emoji standing in for icons (a gear, a lock, a sun and a moon, check marks), and a system font that looked different on every machine. People work in these screens all day (sessions, machines, backlog), so the look has to stay quiet and let the data carry the colour.

## 2. Decisions

| # | Decision |
|---|---|
| 1 | **A framed sheet.** A tinted frame holds the sidebar; the page is a white sheet inside it with a 1px edge and a 16px radius, no shadow. On a phone the sidebar becomes a drawer behind a 48px top bar and the sheet runs to the edges. |
| 2 | **Colour belongs to data and status.** Surfaces, text and the primary button are neutral (near-black primary). One accent (a muted teal) marks links and the current item. Status pills are pale tints with a 4px radius. A destructive button is outlined, not filled. |
| 3 | **Tokens, not values.** Colours, radii, spacing and the sidebar width are custom properties at the top of `web/src/styles.css` (`--ground`, `--surface`, `--primary`, `--accent`, `--radius-card`, `--radius-control`, `--radius-chip`, `--radius-sheet`, `--gap`). A page or component uses a token; it does not write a colour or a radius. The older names (`--bg`, `--surface-2`, `--ok`, `--warn`, `--danger`, `--radius`) stay as aliases so `projects.css` and `remote.css` keep working. |
| 4 | **Dark theme is a second set of the same tokens** with a true neutral ground. Light, dark and system are chosen in the sidebar; both are checked whenever a component adds a colour. |
| 5 | **IBM Plex Sans (400, 500, 600) and Plex Mono (400, 500), self-hosted** in `web/public/fonts/` (latin and latin-ext, woff2, with the SIL Open Font License text beside them), loaded by `web/src/fonts.css`. The API's content security policy is `default-src 'self'` and `style-src 'self'`, so a font from another host or an inline style would be blocked. Mono is for code, paths and ids. |
| 6 | **Icons are SVG, not glyphs.** `web/src/components/icons.tsx` has one `Icon` component (16px, 1.5px stroke, `aria-hidden`) and the `BrandMark`. A new icon is added there. An emoji or a text symbol is never an icon, because it renders differently on each system. Where a control is only an icon, it has an accessible name. |
| 7 | **Class names stay.** The restyle changed values, not markup, so a page's tests and its class hooks did not change. New layout helpers: `.machine-page` (two columns from 1200px), `.setting-row` (see 3), `.table-rows` (see 4). |
| 8 | **Settings pages are rows of "what it is, then its controls".** `SettingsSection` puts the group's name and one line of help on the left (240px) and its card on the right (at most 640px) from 900px; below that it stacks. The workspace settings page uses it for General, Capture, Updates, Remote work and the audit log. |
| 9 | **A wide table becomes one card per row on a phone.** Add `table-rows` to the table and a `data-label` to each cell; from 700px down each cell shows its own label. The machines table uses it. A table with only a few short columns stays a table inside `.table-scroll`. |
| 10 | **A text field in a row of fields shows its name as a placeholder**, besides its `aria-label`, so the row reads without hovering. A visible `<label>` is still the rule for a stand-alone field. |

## 3. What was not done

- Plex Mono for the figures in tables (figures use the Sans digits).
- The account and download pages stay a narrow centred column; they are not settings rows.
- End-to-end specs were not run for the restyle (rule 33); the unit tests, `tsc` and `eslint` were.

## 4. How to check a change

Run the app against the real API or a mock, look at the page at 1440px and at 390px in both themes, and run `npm test`, `npx tsc -b` and `npx eslint src` in `web/`. A page that scrolls sideways at 390px, an emoji used as an icon, or a colour written as a value instead of a token is a defect.
