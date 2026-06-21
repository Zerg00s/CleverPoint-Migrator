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
dotnet publish src/CleverPoint.Migrator.Ux -c Release -r linux-x64 --self-contained true -o publish/linux
```

Verified: the published folder contains the exe with `wwwroot/` beside it (scoped
`*.styles.css` bundle, `*.modules.json`, Fluent `_content`, `_framework`). The bundled runtime
means no .NET install is needed on the target.

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
