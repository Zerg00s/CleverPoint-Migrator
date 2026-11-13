# Building & running CleverPoint Migrator

Two front-ends share one engine (`src/CleverPoint.Migrator.Core`):

- **`src/CleverPoint.Migrator.App`** — the original **WinForms** app (`net8.0-windows`, Windows only).
- **`src/CleverPoint.Migrator.Ux`** — the new **Photino.Blazor + Fluent UI** app
  (`net8.0`, cross-platform; runs on Windows via WebView2 and on Linux/WSL via WebKitGTK,
  which makes it screenshot-testable from WSL).

## Windows

```
cd C:\trash\SharePoint-Migrator\CleverPoint-Migrator

REM New Fluent UI app
dotnet run --project src\CleverPoint.Migrator.Ux

REM Original WinForms app (close it before rebuilding, or build to a temp folder):
dotnet build src\CleverPoint.Migrator.App -o C:\temp\appbuild
C:\temp\appbuild\CleverPoint.Migrator.App.exe
```

The engine test suite (live, console):

```
cd C:\trash\SharePoint-Migrator\CleverPoint-Migrator\tools\CleverPoint.Migrator.TestRunner
dotnet run -- copy-list        REM one scenario
dotnet run                     REM all
```

## WSL (for the Fluent UI app + visual testing)

One-time: `sudo apt-get install -y imagemagick xdotool libwebkit2gtk-4.1-0 libnotify4`

```bash
cd /mnt/c/trash/SharePoint-Migrator/CleverPoint-Migrator
dotnet build src/CleverPoint.Migrator.Ux
bash tools/ui-testing/launch-ux.sh          # launches forced onto XWayland (GDK_BACKEND=x11)
bash tools/ui-testing/shot-ux.sh /tmp/x.png # screenshot the window
bash tools/ui-testing/click-ux.sh 120 158   # click window-relative coords
```

## Publishing the Fluent UI app (self-contained FOLDER — never single-file)

Photino serves `wwwroot` from next to the executable, so a single-file publish breaks at
startup (it self-extracts to a temp dir). Publish a self-contained folder and ship the whole
folder (the csproj errors out if `PublishSingleFile=true`):

```bash
dotnet publish src/CleverPoint.Migrator.Ux -c Release -r win-x64   --self-contained true -o publish/windows   # → CleverPoint.Migrator.Ux.exe
# REQUIRED for Windows browser sign-in: publish the WebView2 helper into a SignInHelper\
# subfolder next to the app, or the app shows "Sign-in helper not found". The MSBuild
# bundle target only copies it into the dev BUILD output, never the publish folder.
dotnet publish src/CleverPoint.Migrator.SignInHelper -c Release -r win-x64 --self-contained true -o publish/windows/SignInHelper
dotnet publish src/CleverPoint.Migrator.Ux -c Release -r linux-x64 --self-contained true -o publish/linux
```

Verified: the published folder contains the exe with `wwwroot/` beside it (scoped
`*.styles.css` bundle, `*.modules.json`, Fluent `_content`, `_framework`) and a `SignInHelper/`
subfolder holding `CleverPoint.Migrator.SignInHelper.exe`. The bundled runtime means no .NET
install is needed on the target. `Sign-App.ps1` does both publishes automatically.

Notes:
- WSLg here runs the **Weston/Wayland** compositor; the GTK window must be forced onto
  **XWayland** (`GDK_BACKEND=x11`, `unset WAYLAND_DISPLAY`) or `xdotool`/`import` can't see it.
  `launch-ux.sh` does this.
- **`pkill -f`/`pgrep -f` self-match (exit 144) — the footgun that wasted real time here:**
  `-f` matches the *whole command line of every process, including the shell running the command*.
  Any command whose text contains `CleverPoint.Migrator.Ux` and also runs `pkill -f
  CleverPoint.Migrator.Ux` kills its **own shell** (exit 144), so the launch/log steps never run
  and it looks like the app crashed (empty `WID`, missing log). It is not the app. Anchor the
  pattern to the exe path so it only hits the real process (argv[0] starts with the path, the
  shell's does not): `pkill -f "^$APP"`. `launch-ux.sh` does this. Launch through the wrapper
  (it also forces `GDK_BACKEND=x11`); a bare direct launch goes native-Wayland and is invisible.
- WebKitGTK under WSLg initializes its viewport shorter than the window; `shot-ux.sh` sizes the
  window to ~545px so screenshots have no dead space. This is a WSL-only artifact — on the real
  Windows WebView2 target the shell fills the window.
- Background `&` launches inside the agent shell report a non-zero code but the app still detaches
  and survives; check with `pgrep -f CleverPoint.Migrator.Ux`.

## Browser sign-in helper (Windows)

The new Fluent UI app can't read the WebView's sign-in cookie itself, so browser
connections use a small WebView2 popup helper. Build it once so the app can find it:

```
dotnet build src\CleverPoint.Migrator.SignInHelper
```

App + certificate and Microsoft sign-in connections need no helper. On Linux/WSL the
helper can't run (WebView2 is Windows-only), so use one of those there.

## Command line (cpm)

`src/CleverPoint.Migrator.Cli` builds `cpm`, a console front-end over the same engine. The
services it shares with the desktop app (settings, connections, run service, site browser,
job file model, agent skill installer) live in `Core/Hosting`. `Sign-App.ps1` publishes it
into the app's publish folder and signs it; the MSI puts that folder on the user's PATH.

```bash
dotnet build src/CleverPoint.Migrator.Cli
src/CleverPoint.Migrator.Cli/bin/Debug/net8.0/cpm --help
src/CleverPoint.Migrator.Cli/bin/Debug/net8.0/cpm agent guide
```

- `CPM_HOME` (or `--home <folder>`) points the app and the command line at another settings
  folder, which keeps tests away from real connections and history.
- A job runs in its own detached process (`cpm job exec <id>`, hidden from help) and logs to
  `logs/job-<id>.log` in the settings folder. `cpm job exec <id> --foreground` runs it in the
  current console for debugging.
- Several processes now share `history.db`. A run records its owner process and a heartbeat;
  `HistoryStore.MarkOrphanedAsInterrupted` only touches runs whose owner is gone. Cancelling
  from another process is a flag the owner reads with its heartbeat.
- Commands, help text and the agent guide all come from the command table in the `Commands`
  folder, and the job schema from the Description attributes in `Core/Hosting/JobSpec.cs`.
  Add a command or a job setting there and the documentation follows.
- The agent skill text is `AgentSkill.Content` in Core. Bump `AgentSkill.Version` when it changes.

### Reports

`Core/Reports` is the report engine, shared by `cpm report ...` and the app's Reports page.

- A report is one class deriving from `ReportDefinition` (in `Reports/Definitions`), listed in
  `ReportCatalog.All`. It declares its ID, scope (tenant, sites or site pairs), settings and tables,
  and implements `RunAsync(ReportContext)`, which scans one site and calls `context.Write(table, row)`
  and `context.Count(name)`. The catalog, the command-line help, the agent guide and the app's
  cards are generated from these classes. Tests for a new report go in the `report` group below.
- `ReportRunner.ExecuteAsync` runs a queued report: several sites at once, one site's failure
  recorded and skipped, a heartbeat and a cancel flag in `history.db` (`report_runs` table), and
  resume. The command line runs it in a detached process (`cpm report exec <id>`, hidden); the
  app runs it in-process through `Ux/Services/ReportHost.cs`.
- `ReportStore` keeps a run's rows in `{settings}/reports/<runId>/`: one NDJSON file per table,
  `sites.ndjson` with how each site ended, `targets.json` with the sites of the run. A site's rows
  are staged and only appended to the tables when the site finishes, so a failed or interrupted
  site leaves nothing behind and resume cannot duplicate rows.
- `TenantSites` lists a tenant through its admin site: the admin site's own list of sites for
  site collections, tenant CSOM for OneDrives. No Microsoft Graph anywhere.
- `Core/Hosting/XlsxWriter.cs` writes multi-sheet Excel workbooks row by row without a library.
- `Reports/StorageMap.cs` builds the tree behind the storage map from a run's tables and lays it
  out as a squarified treemap; `Ux/Components/StorageMapView.razor` draws it. The storage report's
  version numbers come from the `SMTotalSize` and `SMTotalFileStreamSize` fields that ride along
  with each row of a library view (`dotnet run -- storage-probe` shows the raw values).
- `Reports/VersionLimits.cs` reads version history limits at the three levels they exist: tenant
  (admin site, `SPO.Tenant`), site (`site/VersionPolicyForNewLibrariesTemplate`, where a limit of -1
  means "tenant default") and library (`VersionPolicies` on the list). Changing them and queuing
  trim jobs goes through the admin site with tenant CSOM (`Definitions/VersionReports.cs`), because
  SharePoint refuses the site-level job calls for an app-only connection. `dotnet run -- version-probe`
  shows the raw answers.
- `Reports/FileKinds.cs` sorts extensions into nine kinds of file. The list is short on purpose:
  each kind has a color on the map (`--k0` to `--k8` in `app.css`, in the order of `FileKinds.All`),
  and more colors than that cannot be told apart. The storage report's `folderKinds` table is what
  lets the map color the files of a folder that have no box of their own.
- The desktop app takes `--open <route>` to start on a page, for example `--open /report/<runId>`.
- `FileWalker` reads a whole library a page at a time (threshold-safe), `RestBatch` sends up to 50
  GETs in one `$batch` request, `Operations/UniquePermissionScanner` finds items with their own
  permissions (the permissions-only copy uses it too).
- An action is a `ReportDefinition` with `ChangesData`. It must do nothing but read unless
  `context.Apply` is true, and write the same table in both cases ("Would ..." versus what it did).
  `cpm report apply <previewRunId>` and the Apply button start the real run from a finished preview.
  `dotnet run -- report-actions` in the TestRunner runs fix-notebooks and nav-sync for real, on a
  library it creates and on a language test subsite, and puts things back.

### Command line tests

`tools/cli-tests/cli_tests.py` drives the built `cpm` the way a script or an agent does and checks
the JSON and the exit code of every call. It runs in a throwaway settings folder, so real
connections and history are never touched. Python 3 only, no packages.

```bash
dotnet build src/CleverPoint.Migrator.Cli
python3 tools/cli-tests/cli_tests.py                # everything (about 20 minutes)
python3 tools/cli-tests/cli_tests.py cli agent      # no network, under a minute
python3 tools/cli-tests/cli_tests.py --cpm C:\path\to\cpm.exe conn site
```

| Group | Covers | Needs |
|---|---|---|
| `cli` | parsing, help, exit codes, reading and validating job files, an old history database | nothing |
| `agent` | the agent guide and the skill installer, in a fake home folder | nothing |
| `conn` | adding, testing, exporting, importing and removing connections; sign-in errors | the `Secrets` folder |
| `site` | lists, folders, users, the mapping template | the `Secrets` folder |
| `job` | real jobs: every mode, selections, identity mapping, both engines, another tenant | the `Secrets` folder |
| `life` | timeouts, cancel, a killed background process, concurrent readers and writers | the `Secrets` folder |
| `report` | the report catalog and file format; then every report on the sandbox, all sites of the tenant in one run, failing sites, cancel, a killed process, resume, export | nothing for the first half, the `Secrets` folder for the rest |

Run one suite at a time. The engine scenarios, `job` and `life` create and delete lists in the test
subsite; a report that scans the site at that moment sees them come and go and its exact-count
checks fail.

The `job` and `life` groups write lists named `CliT-...` to the sandbox test subsite and the lab tenant. Remove
them with `dotnet run -- cli-cleanup` in the TestRunner folder. The script runs on Windows as
well; point `--cpm` at a Windows build.

## Microsoft sign-in (tenant app)

`Core/Auth/DelegatedTokenProvider.cs` signs the user in through the tenant's own app
registration with MSAL.NET (`Microsoft.Identity.Client` and
`Microsoft.Identity.Client.Extensions.Msal`, referenced by Core). It uses the system
browser and a loopback redirect, so it works on every platform. The token cache is
`msal.cache` in the settings folder, encrypted with DPAPI on Windows. On Linux/WSL it is
an unprotected file, which is acceptable for development only.

`Core/Auth/EntraAppProvisioner.cs` registers the app and opens the admin consent page.
The interactive test scenarios are not part of a run of everything; start them by name,
from Windows so the browser opens on the desktop:

```
cd tools\CleverPoint.Migrator.TestRunner
dotnet run -- app-setup         # find or create the sign-in app, ask for consent
dotnet run -- auth-delegated    # sign in, REST + CSOM, silent renewal from the cache
dotnet run -- app-unattended    # the app-only app with a certificate
set CP_AUTH=delegated           # then any scenario runs as the signed-in user
```

## Seeding mock run history (for UI testing)

To exercise the Run History screen at scale, seed 1000 mock runs into the app's
history database:

```
REM Windows (writes to %AppData%\CleverPoint Migrator\history.db automatically):
cd CleverPoint-Migrator\tools\CleverPoint.Migrator.TestRunner
dotnet run -- seed-history
```

From WSL, point it at the Windows database explicitly:

```bash
CP_HISTORY_DB="/mnt/c/Users/<you>/AppData/Roaming/CleverPoint Migrator/history.db" \
  dotnet run --project tools/CleverPoint.Migrator.TestRunner -- seed-history
```

The mock runs vary in status, engine, sites, lists, dates and item counts, and
each carries a few log rows so the run-detail view has content. They are added
alongside any real runs; clear them later from the app or by deleting the DB.
