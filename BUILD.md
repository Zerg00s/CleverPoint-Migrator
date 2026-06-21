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

Notes:
- WSLg here runs the **Weston/Wayland** compositor; the GTK window must be forced onto
  **XWayland** (`GDK_BACKEND=x11`, `unset WAYLAND_DISPLAY`) or `xdotool`/`import` can't see it.
  `launch-ux.sh` does this.
- WebKitGTK under WSLg initializes its viewport shorter than the window; `shot-ux.sh` sizes the
  window to ~545px so screenshots have no dead space. This is a WSL-only artifact — on the real
  Windows WebView2 target the shell fills the window.
- Background `&` launches inside the agent shell report a non-zero code but the app still detaches
  and survives; check with `pgrep -f CleverPoint.Migrator.Ux`.
