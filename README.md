# CleverPoint Migrator

SharePoint Online list/library migration tool (a focused ShareGate-style
clone). Creator: Denis Molodtsov, CleverPoint Solutions Inc.

Engine: .NET 8, SharePoint REST + CSOM only (no Graph for SharePoint data).
The WinForms UI comes after the backend is fully tested; until then the
TestRunner console drives everything.

## Solution layout

| Project | Purpose |
|---|---|
| `src/CleverPoint.Migrator.Core` | The migration engine: auth, REST client, CSOM, schema/item/file copiers, user resolver, verification. |
| `tools/CleverPoint.Migrator.TestRunner` | Live integration tests against real tenants. `dotnet run -- <scenario ...>` |

## Test scenarios (all passing as of 2026-06-11)

| Scenario | What it proves |
|---|---|
| `auth` | App-only cert auth works for REST + CSOM on source and target tenants. |
| `provision` | Creates the migtest subsite, a 9-field test list (25 back-dated, multi-author items + folder) and a test library (12 files in 4 folder locations, back-dated). Re-runnable; works around the tenant retention policy that blocks list deletion by clearing instead. |
| `copy-list` | Same-site list copy: schema, views, settings, items, folder. Verification: every field, Created/Modified exact, Author/Editor exact. 0 mismatches. |
| `copy-lib` | Same-site library copy: folders, files, custom columns, metadata. Verification includes SHA-256 content comparison per file. 0 mismatches. |
| `copy-crosssite` | Subsite to parent-site copy, full user verification. 0 mismatches. |
| `copy-cross` | Cross-tenant copy (gocleverpointcom -> cleverpointlab): fields + dates verify exactly; unmatched users fall back to a configured account with warnings logged. 0 mismatches. |
| `folder-lab` | Live experiment that decided the folder metadata strategy (below). |
| `inspect` | Debug dump of a list's items. |

Run everything: `cd tools/CleverPoint.Migrator.TestRunner && dotnet run -- provision copy-list copy-lib copy-crosssite copy-cross`

## Hard-won engine knowledge (verified live on real tenants)

1. Author/Editor/Created/Modified preservation on items and documents: set
   the four fields (users as `FieldUserValue`) and call
   `UpdateOverwriteVersion()`. Never `SystemUpdate()` (silently no-ops on
   documents in some tenants).
2. Folder items: this tenant accepts the same single
   `UpdateOverwriteVersion` write (lab-verified, strategy A). Some tenants
   reject user fields on folders with "Invalid data ... read only"; the
   engine then falls back automatically to one `ValidateUpdateListItem` call
   with claims-key users AND locale-formatted dates converted to the web's
   regional timezone (strategy B).
3. Resolve every user BEFORE the first item write. `EnsureUser` executes a
   query on the target context, which flushes half-built items and silently
   drops their field values. The engine pre-resolves all referenced users
   up front (`UserResolver.PreResolveAsync`).
4. `FieldValues` cannot appear inside a CSOM `Include()`; it loads by
   default. LINQ over CSOM collections needs `.AsEnumerable()` first.
5. App/system principals (`i:0i.t|...`) cannot be matched by the people
   picker; a form update containing one fails and takes the valid fields
   down with it. The engine skips them (the migrating app then shows as
   author, the closest equivalent of a system account).
6. SP DateTime read-back is UTC with `Kind=Unspecified`; convert via
   `SpecifyKind(utc).ToLocalTime()` before writing (writing `Kind=Utc` is
   rejected on folder items on some tenants).
7. This tenant has a retention policy: lists with content cannot be
   deleted. Tests clear-and-reuse instead.
8. Traffic decoration: `User-Agent: NONISV|CleverPoint|Migrator/1.0` on
   every request; 429/503 retries honor Retry-After and surface throttle
   events to the caller.

## Migration API engine (verified working 2026-06-11)

`MigrationApi/` in Core implements the second copy engine: SPO Migration API
with SharePoint-provided Azure containers. Flow: provision containers +
queue, AES-256-CBC encrypt every blob (random per-blob IV in blob metadata
"IV", mandatory for provided containers), snapshot each blob (also
mandatory), submit `CreateMigrationJobEncrypted`, decrypt and follow the
progress queue. Test scenario `copy-api` verifies the import with SHA-256 +
metadata comparison: 0 mismatches.

The manifest format was aligned to ground truth by running an AMR export
(`CreateSPAsyncReadJob`, scenario `amr-lab`) against a real library and
mirroring SharePoint's own Manifest.xml. Traps that each cost a failed job:
User elements require SystemId; ModerationStatus takes enum names not
numbers; ListItem requires Name, DocId, DirName (server-relative, no leading
slash), ParentFolderId and Order; SPObject Url is server-relative while
File/Folder/ListItem urls are web-relative.

## Lookups and formatting (verified 2026-06-11)

Lookup fields are IN scope: the schema copier rewires the lookup's List
reference (same list on same-web copies; matched by title cross-web, with a
warning when the referenced list is absent), and item values translate
through display-value maps, so item-id drift between lists does not matter.
Copy the lookup target list before the list that points at it.

Column formatting JSON (`Field.CustomFormatter`) does not survive
`AddFieldAsXml`; the copier sets it explicitly, and also syncs it when
merging into an existing target list. View formatting JSON is set on the
created view. Custom views (query, fields, row limit, formatting) are
asserted by tests on whole-list copies.

## Scale, large files, delta, resume (verified 2026-06-12)

The Migration API engine chunks big libraries into pipelined jobs
(ApiMaxItemsPerPackage, default 200 per Microsoft guidance) and streams one
file at a time (download -> encrypt -> upload + snapshot), so RAM stays flat
and no local disk is used. Verified: 400 files / 25 folders in 4 chunked
jobs, 2.9 min, +26 MB working set; 1,200-item list in 1.7 min.

Files at/above CopyOptions.LargeFileThresholdBytes (default 100 MB) go
through REST chunked upload sessions (StartUpload/ContinueUpload/
FinishUpload, 10 MB slices) up to SPO's 250 GB limit; the Migration API
engine routes them there automatically (its own cap is 15 GB). Verified
with a generated 1,144 MB file: copied in 4.7 min, +29 MB working set,
streaming SHA-256 equality on both sides.

Delta runs key on the persisted source-id -> target-id map (SQLite
item_map), NEVER on titles, so lists with identical titles delta correctly:
verified 3 updated in place + 2 added + 7 visibly skipped, no duplicates.
The next-delta baseline is the max server-stamped Modified seen during the
run (client clocks skew; WSL drift broke the first implementation).
Interrupted runs (cancel mid-copy) resume by skipping source paths already
recorded as Copied. History lives in SQLite (runs, per-item log rows with
clickable URLs, CSV export, ClearHistory).

## Dependencies, permissions, identity mapping (verified 2026-06-12)

Cross-web copies auto-create missing SITE COLUMNS and CONTENT TYPES (same
CT id) on the target web, attach CTs to the target list, and assign each
item's content type by name mapping; delta re-runs treat existing
dependencies as graceful no-ops. Item attachments copy (with preserved
dates re-applied; attachment writes stamp Modified). Unique item
permissions copy behind CopyOptions.CopyPermissions (users via resolver,
groups by name with CSV mapping, role definitions by name). Identity
mapping CSV (Type,Source,Target; User rows take UPN/login/email, Group
rows take principal names) imports/exports with an auto-detect template.
RequestThrottle paces all traffic per tenant host so parallel runs share
one budget, and a 429 anywhere pauses every run hitting that host.

## Pages, versions, healing, filters (verified 2026-06-12, second wave)

Modern pages migrate through the SitePages REST API (checkout ->
SavePageAsDraft -> publish) with URL rewriting into the target web; the
list-item path is unusable (the sanitizer strips link hrefs and
SavePageAsDraft renames files to Title-derived names, both handled).
Version history copies via REST /versions, oldest first, verified with
per-version SHA-256. Self-healing (HealingOptions, off by default) re-runs
targeted incrementals up to 5 times and detects corrupt targets (0-byte or
under half the source size), deletes and re-copies them. Wildcard name
filters and folder-scope copies log what they skip. RequestThrottle paces
all traffic per tenant host (shared across parallel runs; any 429 pauses
everyone), verified by timing tests. Special characters (' & # % + unicode)
round-trip on both engines after switching every path to
GetFileByServerRelativePath/decodedUrl. Item attachments copy with
preserved dates.

## The Windows app

`src/CleverPoint.Migrator.App` (net8.0-windows WinForms): branded shell with
tray integration, circular splash, welcome empty-state, two-pane explorer
with drag-to-migrate, migration wizard (engine choice with guidance, live
color-coded log, gentle cancel, queueing beyond the parallel limit),
history (multi-select status filter, rename, CSV export), settings sub-tabs
(performance tiers, maintenance with clear history/cache, diagnostics
Start/Stop capture producing a shareable zip, About with GitHub links), and
an Azure app-registration wizard that ports Setup-MigrationApp.ps1 into the
UI. Sign-App.ps1 (adapted from the sample project) publishes a
self-contained signed exe with git-tag auto-versioning. Help/issues:
https://github.com/Zerg00s/CleverPoint-Migrator/issues

## Performance

See PERFORMANCE.md in the parent folder for the measured classic-vs-API
comparison and engine recommendations.

## Scope decisions

Taxonomy/managed-metadata fields are out of scope (explicit warnings when
skipped). Migration API list items are not implementable (AMR exports no
ground truth for plain items); the classic engine covers lists. Graph API
is used only by the app-registration wizard (the only management surface);
migrations themselves never touch it.

## Roadmap

Remaining work is tracked in the session task list: content types + site
columns, permissions copy (behind a setting), user mapping import/export,
SQLite history + delta with skipped-item reporting, filters + performance +
throttling tests, Migration API (Azure blob) as an alternative engine, then
the WinForms UI (ShareGate-style source/target pickers with drill-down,
drag and drop, browser or app+cert auth, global vs per-migration settings,
clickable migration log with export).
