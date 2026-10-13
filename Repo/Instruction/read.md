# Winly Start — project rules, plan & architecture

**Winly Start** brings the Windows 10 Start menu back to Windows 11. It is an open-source
(MIT) .NET 8 WPF desktop app that *replaces* the Windows 11 Start menu experience
(Start button click, Windows key, Ctrl+Esc) with a faithful Windows 10 style menu —
left rail, A–Z app list, and a live-tile-style tile board — while following the
Windows 11 theme (light/dark, accent colour, transparency) and using Windows 10 style
visuals (acrylic blur, square corners, slide-up animation, tile hover/press effects).

GitHub: https://github.com/Mahi-BD/WinlyStart  ·  Local: `/home/mahi/Project/WinlyStart`

## Build / run rules (house rules)
1. After any change, **compile it yourself** (`dotnet build src/WinlyStart -c Release`) and
   confirm **0 errors** before calling a task done. On Linux the project cross-compiles with
   `EnableWindowsTargeting=true` (use `~/.dotnet/dotnet` if `dotnet` is not on PATH);
   it can only *run* on Windows 10/11.
2. **Minimal code, minimal RAM.** No third-party UI/MVVM frameworks, no WinForms, no
   Windows App SDK / WinUI, no CommunityToolkit. Plain WPF + P/Invoke + `System.Text.Json`
   (source-generated). Every dependency must justify its working-set cost.
3. **Never harm the OS.** No injection into `explorer.exe`, no DLL/registry patching of
   system components, no killing/suspending shell processes, no modifying HKLM.
   Everything Winly Start does is user-mode, HKCU-only, revertible by closing the app.
   The Windows 11 Start menu is *not* disabled — it is intercepted; quit Winly Start and
   Windows is exactly as before.
4. Runs as a **normal user** (`asInvoker`). Never require or auto-request elevation.
5. **Theme follows Windows 11** (registry, live-updated) — never hard-code a colour that
   Windows exposes. Visual *style* follows Windows 10 (square corners, Segoe MDL2 glyphs,
   acrylic tint, 4 px tile gutters, accent-coloured tiles and list headers).
6. Save instructions to `/Repo/Instruction`, scripts to `/Repo/Script`,
   test/scratch files to `/Repo/Temp`. User data lives in `%LocalAppData%\WinlyStart\`
   (`settings.json`, `tiles.json`, `usage.json`) — never inside the install folder.
7. Keep `README.md` (user-facing) and this file (developer-facing) in sync when behaviour
   or layout changes.

## Target
- Windows 11 (all builds; acrylic via DWM system backdrop on 22H2+, composition-attribute
  acrylic fallback on older builds and Windows 10).
- .NET 8 Desktop Runtime (framework-dependent build) — a self-contained single-file build is
  produced by CI for users without the runtime.

## How the "replacement" works (no OS harm)
| Trigger | Mechanism | Notes |
|---|---|---|
| **Windows key** tap | `WH_KEYBOARD_LL` hook (`Core/StartHook.cs`). Win-down passes through. A lone Win-up is swallowed and re-emitted as a chord — a tagged `VK_CONTROL` tap, then the Win-up — so the shell sees "Win + something" and never opens its own Start; then Winly Start toggles. | All Win+X combos keep working natively (they set `_otherKey` and pass untouched). Auto-repeat while Win is held is one press; a Win-down after >1.2 s of silence is a new press (the key-up was missed behind an elevated window). |
| **Ctrl+Esc** | Same hook; swallowed and toggles Winly Start. | |
| **Start button click** | `WH_MOUSE_LL` hook. If a left-click lands inside the Start button rectangle of **any taskbar** (`Shell_TrayWnd` + every `Shell_SecondaryTrayWnd`; Windows 10: the child window of class `Start`, Windows 11: UI Automation `AutomationId = "StartButton"`; `TaskbarInfo.Bars`, cached 2 s, refreshed on every foreground change and on a near miss) **and the top-level window under the pointer (`WindowFromPoint` → `GetAncestor(GA_ROOT)`) is that taskbar**, it is swallowed and Winly Start toggles — on the monitor of that taskbar. With the menu already open on another monitor the click *moves* it there (`Show`) instead of closing it. | Right-click (Win+X menu) passes through. Works with left- and centre-aligned taskbars. Anything else under the pointer (a full-screen remote desktop, a game, a video, an auto-hidden taskbar behind a maximised window) owns the click: on a full-screen RDP session it is the *remote* Start button. Decided from the window under the point, **not** the foreground window — a low-level hook runs before the click activates anything, so the foreground was still the last-used window (on another monitor, say), which is how 1.5.4 opened the local menu over a remote session (user report 10-14). |
| **Click outside the menu** | Same mouse hook. Any button down while the menu is open at a point outside every visible window of the UI thread (`EnumThreadWindows`: the menu, its context menus, flyouts, tooltips, dialogs) → `Dismiss`. The click itself passes through. | Light dismiss no longer depends on `Window.Deactivated`, which never fires for clicks on the taskbar, the desktop or a no-activate window. `Deactivated` stays as the keyboard path (Alt+Tab). |
| **Anything we missed** (touch gesture, Win key while an elevated window is focused) | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`. When a `Windows.UI.Core.CoreWindow` of `StartMenuExperienceHost.exe` **or `SearchHost.exe`** comes to the foreground, Winly Start sends it a tagged **Escape** (closes it; also counts as *our* input, which unlocks `SetForegroundWindow`) and shows itself; `ShowMenu` retries the foreground grab once after 120 ms (`_fgRetry`) if the first attempt lost. | Slight flicker in this rare path only. `AttachThreadInput` + `SetForegroundWindow` alone did **not** win the focus from the SearchHost window (both menus stayed up) — hence the Escape. **Windows 11 25H2 (build 26200) draws the Start menu inside `SearchHost.exe`, in the same CoreWindow (titled "Search") that also serves the Search flyout** — verified live 2026-10-12. So for SearchHost the fallback skips what it can tell is a search: a Win+S / Win+Q chord seen in the last 1.5 s, or the pointer on the taskbar away from the Start button (search-box click, search-highlights hover). Win+S behind an *elevated* window is the one blind spot: it opens Winly Start instead of Search. |

**Full-screen remote sessions stay remote.** When the foreground window is full-screen *and* is a
remote-control / VM client (class `TscShellContainerClass` = mstsc, or an exe named mstsc, msrdc,
RdClient, vmconnect, WindowsSandbox, VirtualBox, vmware, wfica, CDViewer, AnyDesk, TeamViewer, RustDesk,
RDesk, Parsec, Moonlight — `StartHook.RemoteClients`), the Windows key and Ctrl+Esc pass straight
through: the client forwards them to the far machine, whose own Start menu opens, exactly as without
Winly Start. In a *windowed* session the key is local and opens Winly Start, matching mstsc's default
"Apply Windows key combinations: only when using the full screen". Decided on the Win-*down*
(`_passThrough`) so the matching Win-up is consistent. (mstsc also installs its own `WH_KEYBOARD_LL`
hook in full screen and swallows the Win key before ours sees it — so the Win key was always right;
the Start *click* has no such help and goes through the window-under-pointer rule above.)

**Multi-monitor.** Every open carries the screen point it came from (`StartHook.Toggle/Show` pass a
`POINT`: the click, the pointer for the Win key / Ctrl+Esc / tray icon, the centre of the Windows menu
for the fallback). `StartMenuWindow.Position()` takes the taskbar on that point's monitor
(`TaskbarInfo.BarFor`; a monitor without a taskbar — "show my taskbar on all displays" off — falls back
to the primary taskbar, like Windows), that taskbar's Start button for centring, that monitor's work
area and **that monitor's DPI** (`GetDpiForMonitor`) for the pixel size. `Left`/`Top` are DIPs at the
window's *current* DPI, and WPF handles `WM_DPICHANGED` inside the setter, so a cross-DPI move runs
the placement twice (second pass sees the new scale). `StartHook.MenuMonitor` tells the hook where
the menu is.

**The hooks run on their own thread** (`WinlyStart.Hooks`, a plain `GetMessage` loop). Windows calls a
low-level hook on the thread that installed it and allows it only a few hundred milliseconds;
installed from the UI thread (as before 1.5.2) a busy layout pass or a synchronous app launch missed
that deadline, the key fell through to the Windows 11 menu, and — per the `LowLevelKeyboardProc`
docs — after repeated misses Windows **silently removes the hook** with no notification. Hook
callbacks only marshal to the UI thread (`Dispatcher.BeginInvoke`); nothing in them sends a window
message. A **watchdog** (3 s timer) compares `GetLastInputInfo` with the last hook callback: input
flowing for >2 s with silent hooks means they are gone, and they are re-installed (rate-limited to once
per 30 s, because input aimed at an *elevated* window is legitimately invisible to us).

Limitation: low-level hooks cannot see input while an **elevated** window is focused (UIPI).
In that case the fallback row above kicks in. This is by design — we do not run elevated.

## UI layout (Windows 10 1903 style, 100 % DPI values; WPF scales for DPI)
```
┌──────┬────────────────────────┬──────────────────────────────────┐
│ ≡    │ Recently added         │  Productivity                    │
│      │  ▪ App                 │  ┌──────┐┌──────┐┌──────────────┐│
│      │ Most used              │  │      ││      ││              ││
│      │  ▪ App                 │  └──────┘└──────┘└──────────────┘│
│      │ #                      │  Explore                         │
│      │ A                      │  ┌────┐┌────┐┌──────┐            │
│ 👤   │  ▪ App                 │  └────┘└────┘└──────┘            │
│ 📄   │  ▸ Folder              │                                  │
│ 🖼   │ B …                    │                                  │
│ ⚙    │                        │                                  │
│ ⏻    │                        │                                  │
└──────┴────────────────────────┴──────────────────────────────────┘
  48px      248px + scrollbar        6 tile units (3 medium) or 8
```
- Window 648 × 640 (settings: height, tile columns 6/8, list width). Square corners, no title bar,
  `ShowInTaskbar=false`, topmost while open, hides on deactivate / Esc / Win key.
- **List / tiles divider** (`ListGrip`, 2026-10-12): an invisible 8 px Thumb straddling the right
  edge of the list column; on hover (or while dragging) a 3 px accent bar shows — plain trigger
  Setters, **no storyboards**: the first version faded it with EnterActions/ExitActions and the bar
  stayed on after every drag (the drag's "to 1" animation held the value, the hover exit had already
  fired while the pointer left the strip mid-drag, and nothing ran on drag end — user report, v1.5.4). Dragging sets
  `Settings.ListWidth` (176–480, default 256 = `ListCol` width) live; the tile board keeps its
  columns, so the window grows/shrinks with the list (`ChromeWidth` includes `ListWidth`). Saved on
  drag end like the other grips (`Grip_DragCompleted`).
- **Left rail**: hamburger (expands to labelled 248 px overlay), then bottom-aligned:
  user (name + account picture → Change account settings / Lock / Sign out),
  Documents, Pictures, Settings, Power (Sleep / Shut down / Restart).
- **App list**: "Recently added" (≤ 7 days, top 3 + Expand), "Most used" (top 6 from our
  own launch counters), then `#`, `A`–`Z`, `&` headers. Header click → alphabet jump grid
  (available letters enabled). Start-menu sub-folders show as expandable folders.
  Typing while the menu is open filters the list inline; Enter launches the first match.
  Right-click app → Pin to Start / Unpin, More ▸ (Run as administrator, Open file location),
  Uninstall (opens Apps & features).
- **Tiles**: groups with editable names; sizes Small 1×1 (48), Medium 2×2 (100), Wide 4×2
  (204×100), Large 4×4 (204). 4 px gutters. Accent-coloured background, app icon centred,
  name bottom-left on Medium+. Drag to rearrange (first-fit packing), right-click →
  Unpin / Resize / More / Uninstall. Layout persisted to `tiles.json`.
- **Animation**: open = slide up 24 px + fade in 200 ms (ease-out); close = fade 100 ms.
  Tile hover = light border; press = scale 0.97. List item hover = 10 % overlay.
- **Backdrop**: acrylic. Tint `#CC1F1F1F` (dark) / `#CCF2F2F2` (light), or the accent colour
  when Windows "Show accent colour on Start and taskbar" is on. Solid when transparency is off.

## Windows 11 theme integration (`Core/Theme.cs`)
Registry (HKCU), live-refreshed via `SystemEvents.UserPreferenceChanged`:
- `…\Themes\Personalize\SystemUsesLightTheme` → light/dark (Start follows *Windows mode*).
- `…\Themes\Personalize\ColorPrevalence` → accent on Start.
- `…\Themes\Personalize\EnableTransparency` → acrylic vs. solid.
- `…\DWM\AccentColor` (ABGR) → accent brush.
Settings allow forcing Light/Dark.

## App catalogue (`Core/AppCatalog.cs`)
- **Desktop apps**: `*.lnk` / `*.url` under both Start Menu `Programs` folders
  (`%ProgramData%` and `%AppData%`), first-level sub-folder = Start folder, deeper levels
  flattened into it. Created-time drives "Recently added". Watched with `FileSystemWatcher`.
- **Packaged (Store/UWP) apps**: enumerated from `shell:AppsFolder`; items whose
  parent-relative parsing name contains `!` are AUMIDs. Launched with
  `IApplicationActivationManager` (fallback `explorer.exe shell:AppsFolder\<AUMID>`).
- **Icons**: one path for both — `IShellItemImageFactory::GetImage` → `GetDIBits` (top-down
  BGRA) → frozen `BitmapSource`. Loaded lazily on a single background worker, only at the
  sizes needed (32 list, 24/48/96 tiles), cached per app.

## Memory discipline
- Virtualised, recycling `ListBox` for the app list; icons loaded on demand and frozen.
- No `System.Drawing`, no WinForms `NotifyIcon` (tray icon is 60 lines of `Shell_NotifyIcon`).
- Working set trimmed (`SetProcessWorkingSetSize(-1,-1)`) after the menu hides (setting).
- Release build: `ReadyToRun`, `SatelliteResourceLanguages=en`, `DebugType=none`,
  `UseSystemResourceKeys`, no PDBs shipped.

## Source layout
```
src/WinlyStart/
  WinlyStart.csproj, app.manifest (PerMonitorV2 DPI, asInvoker)
  App.xaml(.cs)          startup, single instance, tray icon, hook wiring
  Core/
    Native.cs            all P/Invoke + COM interop declarations
    ShellIcons.cs        IShellItemImageFactory → BitmapSource
    AppCatalog.cs        AppEntry model + enumeration + usage counters
    Launcher.cs          launch / run-as / open location / uninstall / power / session
    Settings.cs          Settings + TileLayout + Usage models, JSON (source-gen) persistence
    Theme.cs             registry theme/accent → dynamic resource brushes
    StartHook.cs         keyboard/mouse LL hooks + WinEvent fallback (the "replacement")
    TaskbarInfo.cs       taskbar edge/rect, Start button rect (UIA), DPI helpers
    Backdrop.cs          acrylic (DWM system backdrop / composition attribute)
    TrayIcon.cs          Shell_NotifyIcon wrapper
  UI/
    Styles.xaml          Win10 styles: brushes, list item, tile, rail button, scrollbar
    StartMenuWindow.xaml(.cs)  the menu (rail + app list + tiles + search + jump grid)
    TilePanel.cs         Panel that packs tiles into the group grid
    SettingsWindow.xaml(.cs)   options
Repo/Instruction/read.md   this file
.github/workflows/build.yml CI: build on windows-latest, publish zip on tag
```

## Settings (`settings.json`)
`ReplaceWinKey`, `ReplaceStartButton`, `SuppressWindowsMenu` ("Keep the Windows 11 Start menu
closed" = the WinEvent fallback; off → the Windows menu is left alone whenever it appears),
`StartWithWindows` (HKCU Run key), `Theme` (Auto/Light/Dark), `TileColumns` (4–12; 6/8 in the
Settings window, any value by dragging), `MenuHeight`, `ListWidth`, `ShowRecentlyAdded`,
`ShowMostUsed`, `OpenAtCorner`, `TrimMemoryWhenHidden`, `Rail.*` (folders + `Calendar` +
`QuickPower` = Sleep / Shut down / Hibernate buttons under the hamburger; Hibernate shows only when
`IsPwrHibernateAllowed()`), `ProfileImagePath`.

## Verified on Windows (2026-09-08, build 26200.8037, light theme, centred taskbar)
Tested live on a Windows 11 Pro box (self-contained build). Confirmed working:
- Windows key opens/closes the menu and the **Windows 11 menu is suppressed**; Ctrl+Esc opens it.
- Start-button click opens it (button located via UIA at a centred taskbar), with proper foreground.
- Type-to-search filters the list; clicking a tile or list item launches the app; the menu then
  light-dismisses; clicking outside dismisses it. Theme (light/accent) is picked up live.
- ~65–165 MB working set, no exceptions in `error.log`.

Fixes made during that testing (all in v0.1):
- **Win-key mask.** The unassigned `0xE8` dummy key does *not* register as a chord on build 26200, so
  the mask uses `VK_CONTROL`, and the lone-tap key-up is **swallowed and re-emitted** (dummy + tagged
  Win-up) — injecting during the key-up and letting it pass queues the dummy too late.
- Tile press/launch threw because WPF freezes the template's `ScaleTransform`; each tile now gets its
  own mutable transform on first interaction (`EnsureTransform`).
- A short post-show "deactivation grace" re-asserts foreground when Windows churns focus on open, but
  must bail when the menu is already closing (else launching an app re-opened the menu).
- Opt-in trace to `%LocalAppData%\WinlyStart\debug.log` via env `WINLYSTART_DEBUG=1`.

Second round of live fixes (also verified on the box):
- **App-list icons were blank.** `Icon` reads size 32 but `SetIcon` only raised `Icon` for size 24, so
  the async load never refreshed the binding. It now always raises `Icon`.
- **Default icon.** Both the list and the tiles fall back to the Segoe MDL2 `AppIconDefault` glyph
  (`&#xECAA;`) whenever the shell returns no icon — driven by a `DataTrigger` on the source being null,
  so it also covers the moment before an icon loads.
- **Auto-hiding scrollbars.** The thin bar is hidden and fades in while the pointer is anywhere in the
  scrolling area. This needs the ScrollViewer to be *templated* (`AutoHideScroll`): a
  `RelativeSource FindAncestor` binding from inside the ScrollViewer's own template resolves before the
  bar is attached to the tree and the trigger never fires — that approach was tried and does not work.
- **Groups, Windows 10 style.** Dropping a tile below the last group creates a new group; an unnamed
  group shows a "Name group" hint; click the header to name/rename (Enter commits, Esc reverts) and it
  persists to `tiles.json`. `Pack()` now also closes whole empty rows, so the board no longer shows the
  large blank bands that stored high row indices used to leave.

Third round (all verified live):
- **No focus rectangle.** The black outline round the app list was WPF's default focus visual on the
  focused `ListBox` — `FocusVisualStyle="{x:Null}"`.
- **Group header is not an edit field until you click it.** It renders as plain text (with a
  "Name group" hint when empty) and becomes a real bordered text box on click, like Windows 10
  (`TileGroupVm.IsEditing`). Enter commits, Esc reverts.
- **Groups move.** Drag a group header to reorder; a click (no drag) renames instead.
- **Right-click on empty board space** → Add program or file… / Add website… / Settings… / About.
  Added items are stored in `custom.json` as `custom:<guid>` entries, merged into the catalogue by
  `AppCatalog.AddCustom`, launched via the shell (`AppEntry.CustomTarget`), so an .exe, a shortcut,
  any file, or a URL all work and get a tile.
- **Configurable left rail.** `Settings.Rail` drives the rail's middle section (Documents, Downloads,
  Music, Pictures, Videos, Network, Personal folder, File Explorer, Settings, Calendar) — edited from
  the Settings window, matching Windows 10's "Choose which folders appear on Start".
- **Calendar.** Ticking Calendar adds a rail button that opens `CalendarWindow`.

Fourth round (verified live):
- **Close buttons did nothing** on Settings / About / Calendar: they relied on `IsCancel`, which only
  closes windows opened with `ShowDialog()`; these are opened with `Show()`. Each now has an explicit
  `Close()` handler. (InputWindow is modal, so its Cancel was already fine.)
- **Calendar rewritten** (`CalendarWindow`): previous · current · next month side by side, custom
  day cells (not the WPF `Calendar` control, which neither scales nor colours per day). The month
  grids sit in a `Viewbox` so every element — day buttons included — scales with the window; the
  window is resizable and its size is remembered in `calendar.json` (recorded on `SizeChanged` *and*
  snapshotted on `Closing`, so it persists even if the user never resized that session). Clicking a
  day shows a note editor on the right (autosave, 400 ms debounce); days with a note are amber, the
  selected day uses the accent (amber border when it also has a note), today has an accent border.
- **Import / export** of all notes as JSON (exact round trip) or iCalendar `.ics` (one all-day
  `VEVENT` per note; SUMMARY = first line, DESCRIPTION = whole note). Import accepts all-day and
  timed `DTSTART`, unfolds continuation lines, prefers DESCRIPTION over SUMMARY, and appends when a
  day already has a note. Verified: two-event `.ics` imported (merge + timed event), `.ics` exported.

Fifth round — professional redesign + live tiles (verified live):
- **Dialog design system** (`UI/DialogStyles.xaml` + `Core/Theme.cs` `Dlg.*` brushes, driven by
  Windows 11 app-mode light/dark + accent). Settings, About, Add-website and Calendar are rebuilt
  around cards, Windows-11 toggle switches, flat combos, an accent slider, primary/secondary buttons
  and a dark title bar via DWM (`UI/Dialog.cs`). Settings also gained a sticky footer and scrolls.
- **Live tiles** (Windows-10-style, `UI/LiveTiles.cs` + the tile template's second "live face").
  Windows 11 has no live-tile platform, so these are our own faces for apps where we have real data:
  a built-in **Calendar** tile (next note), the **Clock** tile (live time), and **Photos** (a
  slideshow from the Pictures folder). The face slides up over the icon and back, staggered per tile
  by `LivePhase`; a 1 s timer runs **only while the menu is open**. Per-tile on/off via the tile's
  context menu (`Tile.Live`, persisted); small tiles are never live, as on Windows 10.
- **Built-in Calendar app** (`builtin:calendar`) appears in the app list and is pinned by default;
  `Launcher.BuiltinHandler` opens it inside Winly Start.
- **Smoother tile drag.** Dragging now reflows live: the dragged tile parks in the cell under the
  pointer and the others glide out of the way (`AnimatedPack` animates `Canvas.Left/Top`); on drop the
  tile eases into place instead of snapping. Resize/unpin also animate. Group-header drag shows an
  accent insertion line (`GroupInsertLine`) and drops at the indicated index.
- **New app icon** (`Repo/Script/make_icon.py`): a modern accent-gradient plate with four rounded
  white tiles and a soft shadow, rendered at 4× and downsampled; the `.ico` carries 16–256 px frames.

Sixth round (verified live):
- **Calendar stretches.** The month grids no longer sit in a `Viewbox` (which letterboxed a short, wide
  window) — they fill the space and the `UniformGrid` day cells stretch, with the day numbers scaled
  from the actual cell size (`ScaleDayText`).
- **Clicking a day never navigates.** Selecting a day from the leading/trailing month used to re-centre
  all three months; it now only selects. Use the arrows to change month.
- **Note categories.** `CalendarData` gains `Categories` (id/name/#RRGGBB) and `NoteCategories`
  (date → id); the note text stays in `Notes`, so existing files and JSON/ICS import-export are
  unchanged. Day cells take their category colour, the note panel has a category picker, the footer a
  legend, and **Categories…** opens `CategoryWindow` to add / rename / recolour / delete (deleting one
  falls its days back to the first category).
- **Live tile text no longer collides with the tile name**: the live panel reserves the name strip
  (bottom margin), empty title/big parts collapse instead of reserving a line, the big value sits in a
  `StretchDirection=DownOnly` Viewbox so a long time shrinks rather than being cut, and a gradient
  scrim sits behind the name on photo faces.
- **Settings fits without scrolling**: 980 px wide, two columns of cards, plus an **Account picture**
  card (`Settings.ProfileImagePath`) to choose a custom picture or fall back to the Windows one.
  `UserInfo.PicturePath` prefers the override, `UserInfo.WindowsPicturePath` ignores it, and the rail
  reloads via `LoadUserPicture()` on save.

Seventh round — v1.4.0 (verified live):
- **Website tiles get the site's favicon and a live preview.** `Core/WebAssets.cs` fetches the
  favicon (declared `<link rel=icon>`, else `/favicon.ico`) and the page's own preview image
  (`og:image` / `twitter:image`), caching both under `%LocalAppData%\WinlyStart\web`. The favicon
  becomes the tile/list icon and the preview is the live face, refreshed at most every 30 min
  (`WebAssets.ThumbnailLifetime`) while the menu is open. **Note:** we use the image the site
  publishes as its own thumbnail rather than embedding a browser to screenshot the page — that would
  mean a WebView2 dependency and a large working set, against the house rules.
  ⚠️ A cached favicon must be decoded with `ShellIcons.LoadFile` (`img:` prefix): putting the PNG
  through `IShellItemImageFactory` returns the generic "picture file" icon, not the image.
- **The menu opens where the Start button is.** `Position()` now centres the menu on the Start
  button for top/bottom taskbars (a centred taskbar centres the menu; a left-aligned one lands left
  once clamped to the work area). `Settings.OpenAtCorner` forces the old corner behaviour.
- **Start-button click no longer falls through to the Windows menu.** The cached button rectangle
  went stale because the taskbar re-centres whenever an app opens or closes: the cache TTL is now 2 s,
  it refreshes on every foreground change and on any near-miss click, and the hit test has 3 px of
  slack. Verified with 8 consecutive clicks and 6 Windows-key taps — every one opened Winly Start.
- **Calendar month moved into the title bar** ("Calendar — September 2026"), freeing the header row.
- Version is **1.4.0** (csproj + manifest).

Eighth round (verified live):
- **The expanded rail matches the icon rail.** The hamburger view had its own hard-coded Documents /
  Pictures / Settings rows, so it disagreed with the configurable icon rail. Both now bind to the same
  `RailItemVm` list built by `BuildRail()` (`RailList` for the icons, `RailFlyoutList` for the labelled
  rows), with the user row above and Power below.
- **Hairline outline.** A 1 px `Rs.Divider` border (`Outline`, inside `Root` so it fades with the menu)
  separates the menu from the taskbar and the desktop.
- **User-added items are editable.** Tiles and app-list rows for custom items show **Edit…**
  (`AppEntry.IsCustom`), which reopens `InputWindow` pre-filled. That window now doubles as the editor:
  it detects a URL vs a path, shows a **Browse…** button and validates existence for files, and
  validates the scheme for websites. `AppCatalog.UpdateCustom` saves and clears the cached favicon and
  preview when the target changes, so a new site re-fetches its own assets.

Ninth round — v1.5.0 (verified live):
- ⚠️ **Regression fixed: the menu had stopped being resizable.** Rewriting the expanded-rail block in
  the previous round deleted the `TopGrip` / `RightGrip` / `CornerGrip` Thumbs, which sat between the
  rail and the flyouts in the XAML. The handlers still existed, so it compiled with zero warnings and
  the loss was silent. They are restored (corner grip at `ZIndex 96`, above the outline).
- **Much less auto-arranging.** `Pack()` no longer collapses empty rows inside a group: tiles stay in
  the cells they were dropped in, gaps included. Only a gap at the very *top* of a group is closed so a
  group cannot float away from its header, and genuine overlaps are still resolved.
- **Groups are explicit.** The board's right-click menu has **New group**, which creates an empty,
  named group that survives (`TileGroup.KeepEmpty`, persisted) instead of being auto-pruned; dropping a
  tile in clears the flag. The group header has its own context menu — **Rename group** / **Remove
  group** (removing one with tiles asks first).
- Version **1.5.0** (csproj, manifest, installer, CI).

Tenth round — v1.5.1 (verified live):
- ⚠️ **Dragging a tile into another group could silently fail.** The live drag preview kept re-placing
  the tile in the *source* group as the pointer moved past it, which grew that group's canvas over the
  group being aimed at; the drop then hit-tested back into the source. Worst case the tile was left
  stranded on a far row (row 8), leaving a big gap.
  Three changes: the preview only runs while the pointer is over the source group and puts the tile
  back at its starting cell as soon as the pointer leaves; the preview row is clamped to
  `LastContentRow` so a group cannot stretch downwards without limit; and, decisively, every group's
  canvas rectangle is **snapshotted at drag start** (`CaptureGroupRects` / `GroupAt`) so mid-drag
  reflow cannot move the drop targets. The drop is now hit-tested from the **pointer**, not the tile
  centre.
  Verified all three directions: up into an earlier group, down into a later one, and into an **empty**
  group (the case that stayed broken after the first fix).

Eleventh round — v1.5.2 (2026-10-11, smoothness / "menu sometimes misbehaves" report):
User report: the menu sometimes did not close on an outside click, sometimes the *Windows 11* menu
opened instead, the menu sometimes froze after clicking an icon, and in a full-screen Remote Desktop
session the Windows key opened both the remote Start menu and ours. Root causes and fixes:
- ⚠️ **Hooks were on the UI thread.** Every key/mouse event was delivered to the UI thread; while it was
  busy the hook timed out → the event passed through → the Windows 11 menu opened; repeated timeouts
  make Windows drop the hook silently → the original menu then opened *every* time until restart.
  Hooks now live on a dedicated thread (`StartHook.HookThread`) with a watchdog (see the table above).
- ⚠️ **Launching was synchronous on the UI thread.** `LaunchApp` started the 110 ms fade and then called
  `Process.Start` / `IApplicationActivationManager` right there; packaged-app activation can block for
  seconds, so the half-faded menu froze (the "stuck form") and the hooks starved at the same moment.
  Everything that leaves the process now goes through `StartMenuWindow.Open(Action)` = hide, then run on
  the pool (`RunAsAdmin`, `OpenFileLocation`, folders, power actions, `shutdown.exe`'s 5 s wait included).
  Built-in items (`builtin:calendar`) stay on the UI thread. The usage counter is saved with
  `Store.SaveAsync` (serialise now, write on the pool; unique temp file per thread because the rescan
  thread saves the same file).
- **Light dismiss from the mouse hook** (table above) instead of relying on `Window.Deactivated`.
  `ToggleMenu` ignores a toggle within 300 ms of a hook dismiss: the tray-icon click that dismissed the
  menu also arrives as a toggle and used to reopen it.
- **Hide can no longer wedge.** `FinishHide` is idempotent and a 400 ms `_hideGuard` timer runs it if the
  fade's `Completed` never arrives (animation replaced / dispatcher starved). `CancelDrag()` on hide
  releases a tile or group header still pressed (Win key mid-drag left the tile shrunk with the
  capture). Housekeeping after a hide (`ResetTransient`, working-set trim) is deferred to
  `ContextIdle` — the user has just launched something, the system is busy bringing it up.
- **Remote Desktop:** full-screen remote clients own the Windows key (section above); a click where the
  *remote* Start button lands no longer opens the local menu (full-screen foreground check).
- `AttachThreadInput` is skipped when the foreground window `IsHungAppWindow` (joining a hung thread's
  input queue hangs us with it). The account display name (`GetUserNameExW(NameDisplay)`, a domain
  round-trip on AD machines) is resolved off the UI thread and cached. `Prewarm()` shows the window once
  at opacity 0 at startup so the first real open does not pay for layout/font/composition setup.
  `TaskbarInfo` boxes the cached Start-button rect so the hook thread never reads a torn struct.
- Version **1.5.2** (csproj, manifest, installer, MSIX manifest, CI).
- ✅ **Verified live 2026-10-12** on DESKTOP-TG4AQRP (Windows 11 Pro 26200, 1280×800, deployed to
  `D:\WinlyStart\WinlyStart.exe`, driven through the NebulaSSH agent with `Repo/Temp/ws.ps1`): Win tap,
  Ctrl+Esc and Start-button click open the menu with the Windows menu staying closed; a click outside
  (left or right, on the desktop, on another window) dismisses and the click goes through; Win+R held
  opens Run without the menu; six Win taps in 1.3 s leave a consistent state; tile click, list click and
  search+Enter launch Notepad (packaged) and Calculator (Store) with the menu hiding at once and
  reopening 150–180 ms later; working set 23–35 MB hidden. **First open after a fresh download took
  4 s** (Defender scanning the new 145 MB exe + single-file extraction) — after a restart the first open
  takes 174 ms, so it is a one-time cost, not a code path. Full-screen pass-through verified with a
  fake full-screen window from an exe named `mstsc_fake.exe` (a renamed `mshta.exe`): Win tap →
  "remote session owns the key, passing through", Start-button click at the local button → ignored.
- **List / tiles divider** (user request 10-12, see the UI layout section): hover-visible accent bar
  between the app list and the tiles; drag sets `Settings.ListWidth`. Verified live.
- **Settings window no longer clobbers a dragged size.** `ColumnsBox` only offered 6/8 and `Ok_Click`
  wrote `6` for anything else, and the height slider's tick snapping rewrote `MenuHeight` — so
  changing the *theme* shrank a 9-column menu back to 6 and reflowed every tile (user report 10-12).
  Now a custom column count shows as a third "Custom — N units" entry and both values are written
  back only when the user actually changed them (`_loadedColsIndex` / `_loadedHeight`).
- **Dark-mode combo boxes showed their value dark-on-dark** (user report 10-12: "in dark background
  the text should be white"). `DlgComboBox`'s header is a `ToggleButton`, whose default style forces
  the system *ControlText* colour onto its content, so the selected value ignored `Dlg.Text`. Fixed
  with `TextElement.Foreground="{DynamicResource Dlg.Text}"` on that `ContentPresenter`. Second report
  the same day: the **app-list names** were grey in dark mode — the `ListBox` / `ListBoxItem` default
  styles set the system *ControlText* brush, which beat the window's inherited `Rs.Text`; `AppListBox`
  and `AppItem` now set `Foreground = Rs.Text` explicitly (white on dark, black on light — verified).
  Rule: any control with a default WPF style that sets `Foreground` (ListBox, Button/ToggleButton,
  TextBox, ComboBox…) needs an explicit `Rs.Text` / `Dlg.Text` setter; inheritance alone is not enough.
- **"Keep the Windows 11 Start menu closed"** (user report 10-12 with a screenshot of *both* menus
  open): the fallback now keeps closing the Windows menu until it is really gone — `StartHook.NativeMenu`
  remembers the window, `NativeMenuOpen` checks `IsWindowVisible && !IsCloaked` (DWMWA_CLOAKED; a
  closed CoreWindow stays "visible"), and the menu window's `_fgRetry` timer (120 ms, up to 6 ticks)
  sends another tagged Escape and re-grabs the foreground while it is still up. Exposed as
  `Settings.SuppressWindowsMenu` (default on; `FallbackEnabled = SuppressWindowsMenu && (ReplaceWinKey
  || ReplaceStartButton)`).
- **Quick power buttons** (user request 10-12): Sleep (E708) / Shut down (E7E8) / Hibernate (E708 with a
  small E7E8 overlay — MDL2 has no hibernate glyph) under the hamburger in the icon rail and, labelled,
  at the top of the expanded rail. `Rail.QuickPower` toggles them; Hibernate is **disabled** (dimmed,
  tooltip "Hibernation is not available on this PC") when `IsPwrHibernateAllowed()` is false (no
  hiberfile / firmware without S4). Disabled rather than hidden: a vanished button looks like a bug.
  Sleep is always enabled, like the power flyout — the first cut dimmed it on `IsPwrSuspendAllowed()`
  and the user objected at once (the test box is a QEMU VM: `powercfg /a` lists no S1–S4 at all, yet
  the user expects Sleep there). `Launcher.Hibernate` = `SetSuspendState(true,…)`.
- **Windows 10: packaged-app tile icons looked tiny** (user screenshot 10-12, build 19045). Measured on
  that PC with `IShellItemImageFactory` (`Repo/Temp`-style probe): Windows 10 returns packaged-app icons
  **plated** — an opaque square in the app's tile colour with the glyph at only ~25 % of it, at every
  requested size (96 → 24 px, 256 → 66 px, 512 → 131 px); Windows 11 returns the bare glyph on
  transparency. On our accent tile the plate vanished and only the small glyph showed. Fix
  (`ShellIcons.Unplate`, v1.5.3): for tile-sized packaged-app icons (64/96/256 requests, never the 32 px
  list icon, which keeps the plate like Windows 10's own list) detect a plate (≥ 97 % opaque, four
  corners the same colour, 2–85 % of pixels differ from it), make the plate transparent, crop to the
  glyph + 12 % air; and because the glyph is so small, re-fetch at **4×** (≤ 512) when a plate was found
  so the crop stays crisp. Desktop icons and Windows 11 glyphs are untouched (transparent → not plated).
- **Export… / Import…** buttons in the Settings footer (`Core/Backup.cs`). One JSON document:
  `{ app, version, exported, files: { "settings.json": {…}, "tiles.json": {…}, "custom.json": {…},
  "usage.json": {…}, "calendar.json": {…} } }` — the data files embedded verbatim
  (`Utf8JsonWriter.WriteRawValue`), settings taken from the live object. Import parses the whole
  file, round-trips every embedded file through its model (`JsonCtx`) and only then writes them with
  `Store.WriteRaw`; `App.ReloadFromDisk()` re-reads settings (normalised), re-applies theme/hooks/
  autostart, flags the menu to reload `tiles.json` at the next catalog change
  (`StartMenuWindow.ReloadLayout`) and calls `AppCatalog.ReloadUserData()` (custom + usage, rescan +
  web assets). A scan requested while one runs is now queued (`_rescan`) instead of dropped. Cached
  favicons / previews are not in the file; website tiles fetch them again.
- ⚠️ **Windows 11 25H2 hosts the Start menu in `SearchHost.exe`** (found during that test: the
  passed-through Win tap opened the native Start — Pinned / All, Category view — as a
  `Windows.UI.Core.CoreWindow` titled "Search", hwnd shared with the Search flyout). The fallback keyed
  on `StartMenuExperienceHost.exe` never fired there, so with an elevated window focused the Windows
  menu stayed. Fixed the same day (table above): SearchHost accepted, searches skipped by the Win+S/Q
  chord and the pointer-on-taskbar tests, tagged Escape + 120 ms foreground retry because the SearchHost
  window does not give up the focus to `AttachThreadInput`.

Twelfth round — v1.5.5 (2026-10-14, user report: "when remote desktop connect and click start … it
open the main win menu but not the remote menu. also if multi monitor it open the menu on the main …
not secondary monitor"):
- **Start click over a full-screen remote session opened the local menu.** 1.5.4 decided "is the
  taskbar covered?" from the *foreground* window (`FullscreenForeignForeground`). A low-level mouse hook
  runs before the click activates anything, so with two monitors the foreground was still the window
  the user worked in last (Firefox on the other screen) while the click landed on the remote Start
  button inside the full-screen mstsc window — on Windows 10 the remote and local Start buttons sit on
  the same pixels (0,1040 48×40). Now the rule is "the top-level window under the pointer is a
  taskbar" (`WindowFromPoint` + `GetAncestor(GA_ROOT)`, class `Shell_TrayWnd`/`Shell_SecondaryTrayWnd`),
  which is also simpler than the full-screen/`IsZoomed` heuristics it replaces. Probed read-only on the
  user's PCs (NebulaSSH agents `864-348-091-011` DEVAI Win11 26200 and `675-192-591-101` Win10 19045
  with mstsc open): `WindowFromPoint` at the Start button returns root `Shell_TrayWnd` on both; UIA
  `StartButton` lookup 206–462 ms (hence the async cache). ⚠️ PowerShell passes `$null` to a `string`
  P/Invoke parameter as `""`, so a probe's `FindWindowEx(tray, 0, "Start", $null)` found nothing while
  `EnumChildWindows` listed the `Start` child — the C# path (real null) is fine.
- **Multi-monitor.** `TaskbarInfo` now snapshots every taskbar (`Bar` records: hwnd, primary?, rect,
  Start button) instead of one Start rectangle; the hook hit-tests all of them and posts the click point;
  the Win key / Ctrl+Esc post the pointer, the fallback the Windows menu's centre, so the menu opens on
  the monitor the user is working on (Windows 10 behaviour). Clicking the other monitor's Start button
  while the menu is open moves it there. Neither of the user's PCs had a second monitor attached and the
  Win11 test VM was offline, so this is **verified by build + code review only, not live** — first thing
  to test when a two-monitor box is available: open on each taskbar, move between them, mixed DPI
  (125 % + 100 %) placement, "show taskbar on all displays" off.
- Helper `Repo/Temp/npz.sh <agent> <file.ps1>` gzip-packs a script into the 8 KB `cmd` line (plain
  `-EncodedCommand` of a 3.9 KB script was "The command line is too long"); `nup.sh` (connector
  `/upload`) hung for both agents — uploads are not reliable on those agents, use `npz.sh`.

Microsoft Store submission (2026-09-09):
- Product **Winly Start** reserved in Partner Center as an **EXE or MSI app** (unpackaged Win32).
  MSIX was rejected on purpose: a packaged app cannot install the low-level hooks the whole product
  depends on. Product id `f0f7a659-0dc8-4389-919f-e55d8daaed01`.
- ⚠️ **The GitHub release URL cannot be the Store package URL.**
  `github.com/.../releases/download/...` answers **302** with a signed, expiring
  `release-assets.githubusercontent.com` link, and Partner Center refuses it:
  *"The package URL redirects to another URL. Provide a download URL without redirection."*
  The installer is therefore mirrored on GitHub Pages, which serves it **200, no redirect**:
  <https://mahi-bd.github.io/WinlyStart-downloads/WinlyStart-Setup-1.5.1.exe>
  (repo `Mahi-BD/WinlyStart-downloads` — deliberately separate so 50 MB binaries never enter this
  repo's history). **Every release must be copied there and the Store package URL bumped**, or the
  Store keeps shipping the old build.
- Store logos are generated, not photographed: `docs/store/logos/` holds a 1080x1080 box art and a
  720x1080 poster. Partner Center rejects anything smaller, so the mark is *redrawn* at that size by
  the generator rather than upscaled from the 256 px icon.
- Age rating came back **ESRB Everyone / IARC 3+** from the IARC questionnaire (All Other App Types,
  "no" to every content question).
- The certification notes spell out the two user-mode hooks up front - a tester who finds
  `WH_KEYBOARD_LL` without that context is likely to flag the app.
- ⚠️ **The rating sat at "Incomplete" for a non-obvious reason**: the IARC *Terms of Use* consent
  checkbox at the bottom of the ratings summary was unticked, which silently disabled **Save** and
  kept **Submit** greyed out. Answering the questionnaire and previewing is not enough - the box has
  to be ticked and the summary saved.
- ⚠️ **Package validation lies about being stuck.** The page claims ~30 minutes, does not refresh
  itself, and showed Malware + Code sign spinning for over an hour. A hard reload after submitting
  showed both had **passed** long before. Reload before assuming a run has wedged.
- **Submitted 2026-09-09 - and REJECTED the same day under policy 10.2.9 (Package Submissions):**
  the installer is **unsigned**. Every PE file must be signed with a certificate chaining to a CA in
  the Microsoft Trusted Root Program, SHA256 or higher. Nothing else was raised.
- ⚠️ **The pre-flight "Code sign check" is not trustworthy.** It reported *"Your app has a valid code
  sign"* for the exact package certification then classified as **Unsigned**. Do not read that check
  as evidence the binary is signed.
- CI now signs `out/fdd/WinlyStart.exe` and `out/sc/WinlyStart.exe` **before** ISCC (the installer
  embeds the self-contained exe, so signing after would ship an unsigned payload), signs the
  installer afterwards, and fails the build on `signtool verify /pa`. Driven by the repository
  secrets `CODESIGN_PFX_BASE64` / `CODESIGN_PFX_PASSWORD`; without them the build still succeeds and
  warns, so forks and PRs keep working. **A real certificate is still needed** - self-signed will not
  pass. See `docs/microsoft-store.md` for the options.

Known rough edge: **acrylic renders as a solid tint on build 26200** — `SetWindowCompositionAttribute`
no longer blurs on current Windows 11. Real blur needs the DWM SystemBackdrop path (roadmap). The
surface still tints correctly to the theme.

## Roadmap
- v0.1 (this scaffold): everything above, static tiles, single primary-monitor taskbar.
- v0.2: ~~multi-monitor (open on the monitor whose Start button was clicked)~~ (done in 1.5.5), group drag,
  keyboard navigation polish, jump-list on right-click (recent files), localisation.
- v0.3: MSIX/winget package, Start folder ("Windows Tools") virtual folders, tile badges.
- Not planned: true live tiles (the platform API no longer exists on Windows 11).
