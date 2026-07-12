# MacDesk

**English** | [简体中文](README.zh-CN.md)

A macOS-style desktop layer for Windows. MacDesk draws its own icon grid on a
full-screen layer that sits **above the wallpaper and below every application
window** (the same z-order as Wallpaper Engine), and stores every icon position
as a **resolution-independent anchor distance** (top-right / near-edge anchored,
the macOS model). When the resolution, DPI, or display configuration changes,
icons keep their relative layout and re-flow smoothly — the way they do on
macOS, instead of collapsing into the top-left corner the way the native
Windows desktop does.

> Status: works on Windows 11 (tested on 24H2 / build 26200), including
> multi-monitor with mixed DPI (verified on 100% + 225%, up to 4K TV setups).
> The project began as a personal tool and the design notes live in
> [`docs/dev-notes.zh.md`](docs/dev-notes.zh.md) (Chinese).

## Install

Grab the latest release from the
[Releases page](https://github.com/Nishikinonakai/MacDesk/releases):

- **`MacDesk-Setup-vX.Y.Z.exe`** (recommended) — per-user installer, no admin
  prompt. Upgrades quit the running copy gracefully (restoring native icons
  before swapping files); uninstall restores the native desktop and cleans up
  autostart. Your layout and settings are never deleted.
- `MacDesk-vX.Y.Z-win-x64.zip` — portable. Unzip and run `MacDesk.exe`.
  Quit a running MacDesk before installing over it.

Updating later is one click: **Settings → About → Check for Updates** downloads
the new installer and replaces itself silently, then relaunches.

## Features

- **One relative layout that survives display changes** (the macOS model). Each
  icon is stored **once** as a resolution-independent anchor distance (fixed
  spacing, top-right / near-edge anchored). On a resolution or DPI change the
  display position is *derived* and the stored layout is **never rewritten**, so
  every resolution shows the *same* arrangement — switching 1080p ↔ 4K ↔ 720p and
  back is byte-for-byte identical. When a screen is too small to hold every row,
  overflow icons wrap into more columns (concentrate) instead of being lost;
  a bigger screen keeps the same spacing and simply leaves more empty space.
- **Live wallpaper (Wallpaper Engine) support.** When Wallpaper Engine is
  running, MacDesk adopts its per-monitor render window into the desktop layer
  — native rendering, zero extra cost, parallax and click-through interactions
  intact (you can press buttons on interactive web wallpapers *through*
  MacDesk). WE exiting or restarting is detected and handled automatically.
  While a live wallpaper is active the icon layer is rendered through a
  dirty-region pipeline that only re-rasterizes what changed, keeping stack
  animations near 60 fps at 1080p. Performance toggles (disable icon shadows /
  animations) are available for low-end machines.
- **Real desktop interactions**, drawn by MacDesk but backed by the Windows
  shell: double-click to open, native right-click menus (item **and** desktop
  background — "New", "Paste", third-party shell extensions), inline rename,
  delete to Recycle Bin, marquee + Ctrl multi-select, group drag with grid snap
  and collision avoidance, and OLE drag-and-drop in/out of other windows.
  Cancelled drags spring back to their origin, Finder-style.
- **Rock-solid native context menus.** Shell menus are built in an isolated
  helper process (a crashing third-party extension can't take the desktop
  down), then serialized and shown **on the main UI thread** — immune to the
  async foreground battles that make popup menus vanish for other desktop
  overlays. Menus follow the system dark mode, support full keyboard
  navigation, and a settings GUI lets you blacklist unwanted entries.
  If [Locale Emulator](https://github.com/xupefei/Locale-Emulator) is
  installed, a "Run with Locale Emulator" item is added for executables.
- **Use Stacks** (background menu) — macOS-style auto-grouping, with a **Group
  By** submenu for Kind / Date Modified / Size. Closed piles fan out up to
  three real member icons; hover-scrub a pile to cycle its front icon through
  members. Clicking a pile flies its members out with a spring animation;
  clicking again gathers them back. Doesn't touch the canonical layout —
  turning Stacks off restores your exact arrangement.
- **Folder Stacks** — in Stacks mode, right-click a desktop folder and pick
  **Display as Stack**: the icon gets a chevron badge, and a single click
  expands the folder's contents in place as real icons (open / drag out /
  right-click), click again to collapse (macOS Dock folder-stack semantics).
  Dropping files on the folder still moves them in; dragging an expanded item
  to empty desktop moves it out. Want custom grouping? Make a folder and flag
  it — the group IS the folder, so it survives even uninstalling MacDesk.
- **Desktop icon toggles** — show or hide This PC, User's Files, Network,
  Control Panel and the Recycle Bin from Settings; the first run follows your
  native desktop's current choices.
- **Layout safety.** A rolling daily backup of the layout file (7 kept) plus
  **Export / Import Layout** in Settings for machine migrations. Imported
  entries whose file doesn't exist on the new machine show as macOS-style
  question-mark placeholders — remove them via right-click; MacDesk never
  deletes layout data on its own.
- **Finder-style labels** — long names truncate in the middle, keeping the
  extension visible.
- **Clipboard file operations** — Ctrl+C / Ctrl+X / Ctrl+V via the shell
  clipboard; cut items dim until pasted.
- **Sort / clean-up** — "Clean Up (mac-style grid)" and a **Sort By** submenu
  (name / date / size / kind), both one-shot and undoable.
- **Free placement mode** — macOS `arrangeBy=none`: drop icons exactly where
  you release them.
- **Work-area-aware grid** — the grid reads the taskbar's real size and position
  (any edge, small icons, hidden or auto-hidden) and claims exactly the usable
  space — no guessed reserves.
- **Sink First Row** (Settings → Desktop) — optional half-row top inset so
  third-party top menu bars don't overlap the first icon row.
- **Multi-monitor, the macOS way.** One desktop window per monitor (mixed DPI
  handled per window). Icons *belong* to a monitor; drag them across screens to
  move them. Unplug a monitor and its icons consolidate onto the primary display
  — without their stored positions being touched — then return to their exact
  spots when it's reconnected. Monitors are identified by EDID.
- **Survives Explorer restarts.** A tiny windowless watchdog relaunches the
  desktop layer within ~250 ms if Explorer restarts or the main process dies.
  Resolution changes are a seamless process handoff with zero bare-desktop
  flash.
- **macOS-style Settings window** — sidebar + content pages following the
  system light/dark theme: autostart (with a fast scheduled-task mode),
  **interface language (system / English / 简体中文)**, accent color,
  live-wallpaper performance toggles, context-menu blacklist, layout
  export/import, and an About page with a one-click updater (the app's only
  network access — no backend, no telemetry).
- **First-run onboarding** — on a fresh install MacDesk asks whether to import
  your existing desktop arrangement or start with a clean mac-style top-right
  flow.
- **Lean at scale** — icons of the same file type share one bitmap, so even a
  desktop with hundreds of files starts in seconds and stays light on memory.
- **High-DPI aware** — correct rendering at 100%–300% and mixed-DPI transitions.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| Double-click | Open item |
| Enter / F2 | Rename (mac-style: the base name is selected) |
| Delete / Backspace | Move to Recycle Bin |
| Ctrl+A | Select all |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / cut / paste files |
| Ctrl+Shift+N | New folder (enters rename immediately) |
| Arrow keys | Move selection across the grid |
| Type a name | Type-ahead selection |
| F5 | Refresh |
| Esc | Clear selection / cancel cut |
| Ctrl+Alt+Q | Quit MacDesk |

## Build

Cross-compiles to `win-x64` from macOS or Linux (`EnableWindowsTargeting` is set):

```bash
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

Requires the .NET 10 SDK. CI builds the artifact on every push and attaches
the zip + Inno Setup installer to the release on tag pushes
(`.github/workflows/build.yml`, `installer/macdesk.iss`).

## Run

```
MacDesk.exe                 # attach to the desktop (SHELLDLL_DefView)
MacDesk.exe --hide-native   # also hide the native Explorer icon list (restored on quit)
MacDesk.exe --quit          # gracefully quit a running instance (and its watchdog)
```

To quit: the **Quit** button in Settings → Advanced, **Ctrl+Alt+Q**, or
`--quit`. Because MacDesk is meant to be always-on, force-killing the main
process makes the watchdog relaunch it — use one of the above to stop it fully.

Data files (per user, `%LOCALAPPDATA%\MacDesk\`): `layout.json` (canonical
icon positions, plus rolling backups under `backups\`), `settings.json`.
The runtime log is `macdesk.log` next to the executable.

## Architecture

| File | Responsibility |
|---|---|
| `App.xaml.cs` | Process lifecycle: single instance, CLI verbs, watchdog spawn, clean-quit protocol |
| `Desktop.cs` | Multi-monitor coordinator: shared services, per-monitor icon partition |
| `Interop/DesktopLayer.cs` | Attaches the window under the desktop icons (`SHELLDLL_DefView`), survives Win+D |
| `Services/WallpaperEngine.cs` | Wallpaper Engine render-window adoption (discovery, z-order, release) |
| `Services/UlwPresenter.cs` | Layered presenter for the live-wallpaper mode (dirty-region frame pushes) |
| `Services/MenuHost.cs` / `MenuSnapshot.cs` / `NativeMenuPresenter.cs` | Isolated shell-menu capture → main-thread native menus |
| `Services/LayoutStore.cs` | Canonical layout (DIU anchor distances), backups, export/import |
| `Services/IconLoader.cs` | High-res shell icons with per-type sharing |
| `Services/Watchdog.cs` | Relaunch on Explorer restart / crash |
| `Services/UpdateCheck.cs` | Release check (rate-limit-proof) + one-click update |
| `Services/L.cs` | Bilingual UI strings (system / en / zh) |
| `MainWindow.xaml.cs` | Layout engine, stacks, drag/snap, keyboard, clipboard, rename |

## Known constraints

- Without Wallpaper Engine the desktop layer is **opaque** and mirrors the
  system wallpaper itself (a WPF transparent child window under `SetParent`
  does not composite); with WE running, the real live wallpaper shows through.
- Right-click menus are built in an isolated child process on purpose:
  `QueryContextMenu` loads every installed shell extension, and a bad one
  fails fast (`c0000409`) hard enough to bypass managed exception handling.
- A resolution change is handled by a seamless process handoff — attaching
  fresh is reliable at any DPI; live re-mount is not.

More hard-won implementation constraints (don't regress them on a refactor) are
in [`docs/dev-notes.zh.md`](docs/dev-notes.zh.md).

## License

[MIT](LICENSE).
