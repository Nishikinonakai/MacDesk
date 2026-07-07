# MacDesk

A macOS-style desktop layer for Windows. MacDesk draws its own icon grid on a
full-screen layer that sits **above the wallpaper and below every application
window** (the same z-order as Wallpaper Engine), and stores every icon position
as a **resolution-independent anchor distance** (top-right / near-edge anchored,
the macOS model). When the resolution, DPI, or display configuration changes,
icons keep their relative layout and re-flow smoothly — the way they do on
macOS, instead of collapsing into the top-left corner the way the native
Windows desktop does.

> Status: works on Windows 11 (tested on 24H2 / build 26200), including
> multi-monitor with mixed DPI (verified on 100% + 225%). The project began as a
> personal tool and the design notes live in
> [`docs/dev-notes.zh.md`](docs/dev-notes.zh.md) (Chinese).

## Features

- **One relative layout that survives display changes** (the macOS model). Each
  icon is stored **once** as a resolution-independent anchor distance (fixed
  spacing, top-right / near-edge anchored). On a resolution or DPI change the
  display position is *derived* and the stored layout is **never rewritten**, so
  every resolution shows the *same* arrangement — switching 1080p ↔ 4K ↔ 720p and
  back is byte-for-byte identical. When a screen is too small to hold every row,
  overflow icons wrap into more columns (concentrate) instead of being lost;
  a bigger screen keeps the same spacing and simply leaves more empty space.
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
  installed, a "Run with Locale Emulator" item is added for executables
  (LE's own handler is a .NET Framework COM extension that cannot be loaded
  in-process).
- **Finder-style labels** — long names truncate in the middle, keeping the
  extension visible.
- **Keyboard & selection parity** — see the table below.
- **Clipboard file operations** — Ctrl+C / Ctrl+X / Ctrl+V via the shell
  clipboard (`CF_HDROP` + *Preferred DropEffect*); cut items dim until pasted.
- **Sort / clean-up** — "Arrange to mac grid" and a **Sort By** submenu
  (name / date / size / kind), both one-shot and undoable.
- **Free placement mode** (optional) — macOS `arrangeBy=none`: drop icons exactly
  where you release them, no grid snapping. Off by default; toggle in the
  background menu.
- **Multi-monitor, the macOS way.** One desktop window per monitor (mixed DPI
  handled per window). Icons *belong* to a monitor; drag them across screens to
  move them. Unplug a monitor and its icons consolidate onto the primary display
  — without their stored positions being touched — then return to their exact
  spots when it's reconnected. Monitors are identified by EDID (stable across
  cable/port changes).
- **Survives Explorer restarts.** A tiny windowless watchdog process relaunches
  the desktop layer within ~250 ms if Explorer restarts or the main process
  dies, and re-attaches to the fresh shell. Resolution changes recover the same
  way.
- **Start on boot** — a checkbox in the background menu (or `--enable-autostart`).
- **Settings window** ("MacDesk 设置…" in the background menu) — free placement,
  autostart, menu mode, and the context-menu blacklist, all editable in a GUI
  (stored in `%LOCALAPPDATA%\MacDesk\settings.json`).
- **High-DPI aware** — correct rendering at 100 %–225 % and mixed-DPI transitions.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| Double-click / (open via native menu) | Open item |
| Enter / F2 | Rename (mac-style: the base name is selected) |
| Delete / Backspace | Move to Recycle Bin |
| Ctrl+A | Select all |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / cut / paste files |
| Ctrl+Shift+N | New folder (enters rename immediately) |
| Arrow keys | Move selection across the grid |
| Type a name | Type-ahead selection (IME is bypassed on the desktop) |
| F5 | Refresh |
| Esc | Clear selection / cancel cut |
| Ctrl+Alt+Q | Quit MacDesk |

## Build

Cross-compiles to `win-x64` from macOS or Linux (`EnableWindowsTargeting` is set):

```bash
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

Requires the .NET 10 SDK. CI builds the same artifact on every push
(`.github/workflows/build.yml`).

## Run

```
MacDesk.exe                 # opaque mode (self-painted wallpaper), attaches to SHELLDLL_DefView
MacDesk.exe --hide-native   # also hide the native Explorer icon list (restored on quit)
MacDesk.exe --quit          # gracefully quit a running instance (and its watchdog)
MacDesk.exe --enable-autostart [mode flags]   # register HKCU\...\Run
MacDesk.exe --disable-autostart
```

To quit: the **Quit** item in the desktop background menu, **Ctrl+Alt+Q**, or
`--quit`. Because MacDesk is meant to be always-on, force-killing the main
process makes the watchdog relaunch it — use one of the above to stop it fully.

Data files (per user, `%LOCALAPPDATA%\MacDesk\`):

- `layout.json` — `{ "version": 4, "monitors": { "<edidKey>": { "<name>": { RightDist, EdgeDist, FromBottom } } } }`
- `settings.json` — `{ "FreePlacement": bool }`

The runtime log is `macdesk.log` next to the executable.

## Architecture

| File | Responsibility |
|---|---|
| `App.xaml.cs` | Process lifecycle: single instance, CLI verbs, watchdog spawn, clean-quit protocol |
| `Desktop.cs` | Multi-monitor coordinator: shared services, per-monitor icon partition, cross-window ops |
| `Interop/Monitors.cs` | Monitor enumeration with stable EDID identity (active-child selection, settle-wait) |
| `Services/Watchdog.cs` | Windowless sibling process that relaunches the desktop layer on Explorer restart / crash |
| `Interop/DesktopLayer.cs` | Attaches the window under the desktop icons: `SHELLDLL_DefView` under `Progman` (24H2+/classic) or a `WorkerW` (Win8–11 23H2); `WS_CHILD` + `SetParent`, survives Win+D |
| `MessageWindow.cs` | Hidden top-level window: receives `WM_DISPLAYCHANGE`, the Ctrl+Alt+Q hotkey, and owns native menus |
| `Services/DesktopItemProvider.cs` | Merges the user + public desktop; `FileSystemWatcher` for changes |
| `Services/IconLoader.cs` | `IShellItemImageFactory` high-res icons; manual `GetDIBits` → BGRA to keep the alpha channel |
| `Services/ShellContextMenu.cs` | Native `IContextMenu` forwarding (runs in an isolated child process so third-party extensions can't crash the host) |
| `Services/LayoutStore.cs` | Single resolution-independent canonical layout (DIU anchor distances) |
| `Services/Autostart.cs`, `Services/Settings.cs` | Boot autostart and user settings |
| `MainWindow.xaml.cs` | Layout engine (right-anchored 112×112 mac grid), drag/snap, keyboard, clipboard, rename |

## Known constraints

- The desktop layer is **opaque** and repaints the wallpaper itself (a WPF
  transparent child window under `SetParent` does not composite). Solid-color
  wallpapers are read from `HKCU\Control Panel\Colors\Background`.
- Right-click menus run in a one-shot child process on purpose: `QueryContextMenu`
  loads every installed shell extension, and a bad one fails fast (`c0000409`)
  hard enough to bypass managed exception handling.
- A resolution change is handled by relaunching (attaching fresh is reliable at
  any DPI; live re-mount is not) — expect a sub-second flash.

More hard-won implementation constraints (don't regress them on a refactor) are
in [`docs/dev-notes.zh.md`](docs/dev-notes.zh.md).

## License

[MIT](LICENSE).
