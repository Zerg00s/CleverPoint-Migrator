# CleverPoint Migrator - request tracker

Every feature Denis asked for, with implementation status. Maintained by
Claude on every working session; check items off ONLY after a live test or
Denis's confirmation. Add new requests the moment they arrive.

Legend: [x] done and verified, [~] done, awaiting Denis's verification on
Windows, [ ] not done yet.

## Engines and fidelity

- [x] Cert app-only auth (client secret is rejected by SPO by design)
- [x] Classic copy engine: lists, libraries, files, folders, items
- [x] Same-site, cross-site (subsites), cross-tenant copies
- [x] Preserve Created/Modified/Author/Editor on items, files, folders
- [x] Version history copy with selectable depth (1/5/10/50)
- [x] List item attachments (with dates re-applied after)
- [x] Content types + site columns auto-dependency copy
- [x] Lookup fields (cross-list translation maps)
- [x] Column/view formatting JSON; views; list settings
- [x] Modern pages + page libraries (SitePages API, sanitizer-safe)
- [x] Item/file/folder unique permissions (optional)
- [x] Migration API engine (Azure blob, encrypted, chunked, pipelined)
- [x] Hybrid large files (>100 MB streams; tested to 1 GB+; 50 GB by design)
- [x] Special characters in names/URLs/columns/views (# % etc.)
- [x] Throttling: shared budget, 429 pause across parallel runs, decoration
- [x] Content-only copies NEVER touch target schema (verified: sentinel col
      survives, no leaked source columns)
- [~] Browser cookie auth end-to-end writes (REST digest + CSOM digest both
      fixed; first-file metadata read-back probe warns when a site ignores
      preserved fields - Denis to re-verify on Windows)

## Selection and scoping

- [x] Copy whole lists/libraries
- [x] Surgical selection: any mix of files AND folders (SelectedPaths)
- [x] Selected list items by ID
- [x] Single-folder scope
- [x] Date filters per task (modified after/before, advanced dialog)
- [x] Run scope persisted with each run (incrementals stay scoped)

## Delta / resume / healing

- [x] Incremental deltas keyed by persisted item map (never Title)
- [x] Server-stamped Modified baseline (immune to clock skew)
- [x] Delta shows skipped items in the log
- [x] Resume interrupted runs (skip already-copied paths)
- [x] History: "Run incremental" + "Open task" (return to session)
- [~] Self-healing: auto re-run incrementals (max 5) + corrupt-file
      detect/re-copy; Settings > Advanced toggles, off by default (engine
      verified earlier via healing scenario; UI wiring new)

## Identity mapping

- [x] Mapping dialog: one row per mapping, User and Group types
- [x] Searchable source/target user pickers fed from site users (no Entra)
- [x] Built-in System Account target
- [x] Unresolved-user fallback choice
- [x] CSV import/export + sample CSV (engine verified live: usermap 29/29)

## History and reporting

- [x] SQLite history (2000+ runs), rename, search, status multi-select
      filter, sortable columns, status colors (status-string aware)
- [x] Per-run item log: filter chips, colors, clickable item links
- [x] CSV export from History AND from the task screen; exports offer to open
- [x] Started / Finished / Duration columns; friendly status text
- [x] Selective delete of history entries (multi-select)
- [x] Copy text out of any table (Ctrl+C + right-click menu)
- [x] Compare report engine (CompareReport; scenario verified)
- [ ] Compare report button in the UI (run it from History/task screen)
- [ ] ETA percent once scan total is known (rate + elapsed shown today)
- [ ] Clickable item links in the LIVE wizard log (history log has them)

## Explorer (Explore & copy)

- [x] Split source/target panes, searchable connection combos, drag-drop
- [x] Subsite drill-down; generic lists show their items
- [x] Threshold-safe browsing of 100K libraries (RenderListDataAsStream)
- [x] Up button + ".." row + Backspace; labels say where they go
- [x] Refresh per pane (bypasses cache); auto-refresh after a run
- [x] Created/Modified/Created by/Modified by columns
- [x] Copy button centered below both panes
- [x] Browse errors land in the status bar, never a crash dialog

## Wizard (task screen)

- [x] Separate "Copy structure + content" / "Copy content only" actions;
      content-only is the single primary action when a target list is open
- [x] Content-only refuses to create a missing target list
- [x] Target list Title AND URL, pre-populated from the source pick
      (same-site suggestions get " - Copy")
- [x] Engine choice with recommendations (benchmarks in PERFORMANCE.md)
- [x] Per-task: versions, user mapping, date filter, export results
- [x] Per-task settings TEMPLATES: save as / apply (JSON, reusable)
- [x] Buttons aligned, one size recipe; both disabled while running
- [x] 401/403 invalidates browser session with a friendly retry hint
- [ ] Field mapping per task (source column -> target column)

## App shell and polish

- [x] Nav menu highlights the active screen
- [x] X hides to tray (runs keep going); quit only via tray Exit
- [x] Tray, toasts (suppressible), splash, programmatic icons
- [x] Real pictogram icons (folder/PDF/image/pages), not letter tiles
- [x] Settings: General/Connections/Performance/Advanced/Maintenance/About
- [x] Advanced tab has real settings now (self-healing toggles)
- [x] Parallel-migrations setting explained inline; queueing note
- [x] Connection test-on-add, reconnect, expiry display, launch health sweep
- [x] In-app Azure app provisioning wizard
- [x] Diagnostics Start/Stop capture (zip), DbgView trace logging
- [x] Update check + About + GitHub Help/Issues links
- [x] Code signing script + auto-version (Azure Trusted Signing)
- [x] Git repo with secrets excluded
- [ ] 100K-item endurance test (deferred to fine-tuning per Denis)

## Verified-by-test scenarios (TestRunner)

auth provision copy-list copy-selected copy-paths content-only browse-large
copy-lib copy-cross copy-crosssite copy-api amr-lab scale-* bigfile delta
resume ctypes perms usermap xt-api compare filters throttle healing pages
chars versions bench (latest sweep: 29/29 on copy-list copy-paths delta
resume usermap; content-only 5/5; browse-large 3/3)
