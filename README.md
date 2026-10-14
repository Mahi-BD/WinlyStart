# Winly Start

[![build](https://github.com/Mahi-BD/WinlyStart/actions/workflows/build.yml/badge.svg)](https://github.com/Mahi-BD/WinlyStart/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/Mahi-BD/WinlyStart?display_name=tag&sort=semver)](https://github.com/Mahi-BD/WinlyStart/releases/latest)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)

**[winlystart website](https://mahi-bd.github.io/WinlyStart/)** · **[download](https://github.com/Mahi-BD/WinlyStart/releases/latest)**

**The Windows 10 Start menu, back on Windows 11.**

Winly Start is a small, open-source (MIT) .NET 8 app that brings the Windows 10 Start menu
experience to Windows 11 — the left rail, the A–Z app list with folders and the alphabet
jump grid, live tiles and the tile board — while following your Windows 11 theme (light/dark,
accent colour, transparency) and keeping the Windows 10 *look*.

It **replaces** the Windows 11 Start menu in everyday use: the Start button, the Windows
key and Ctrl+Esc all open Winly Start. It does this without touching Windows itself — quit
the app and Windows 11 is exactly as it was.

![The Winly Start menu](docs/screenshots/menu.png)

## Install

Download from the [latest release](https://github.com/Mahi-BD/WinlyStart/releases/latest):

| Download | What it is |
|---|---|
| **`WinlyStart-Setup-x.y.z.exe`** | **Recommended.** Per-user installer — no administrator rights, nothing needed on the machine. Adds Start-menu and optional desktop shortcuts, and can start Winly Start when you sign in. |
| `WinlyStart-win-x64-selfcontained.zip` | Portable single .exe. Needs nothing installed. |
| `WinlyStart-win-x64.zip` | Portable single .exe, needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). |

After installing, a tray icon appears — press the Windows key. Right-click the tray icon for
**Settings…**. To uninstall: Windows Settings → Apps, or tray icon → **Exit** and delete the
folder. Your tiles and settings live in `%LocalAppData%\WinlyStart` and are left in place.

## Features

### The menu
The familiar Windows 10 layout: a left rail, "Recently added" and "Most used", the A–Z list
with expandable Start-menu folders, and a tile board with groups.

Type anywhere to search; press Enter to launch the first match.

Hover the boundary between the app list and the tiles and a divider appears — drag it to give the
list more or less room. The width, like the menu size, is remembered.

![Type to search](docs/screenshots/search.png)

### Live tiles
Windows 11 removed the live-tile platform, so Winly Start provides its own faces for the
things it has real data for: a **Calendar** tile showing your next note, a **Clock** tile,
a **Photos** slideshow from your Pictures folder, and **website tiles that show the site's
own preview image**. Each tile has a *Turn Live Tile on/off* switch in its right-click menu.

### Tiles you can arrange
Drag tiles to rearrange them — the others glide out of the way. Resize between Small, Medium,
Wide and Large, drag a tile below the board to start a new group, drag a group header to
reorder groups, and click a group header to name it.

![Tile options](docs/screenshots/tile-menu.png)

### Add anything to Start
Right-click empty space on the tile board to **add any program, file, shortcut or website**.
Websites use the site's favicon as their icon and its own preview image as a live tile, and
anything you add can be renamed or re-pointed later with **Edit…**.

### The expanded rail
Click the hamburger for the labelled rail. Choose which shortcuts appear — Documents,
Downloads, Music, Pictures, Videos, Network, Personal folder, File Explorer, Settings and a
Calendar — in Settings. Sleep, Shut down and Hibernate buttons sit right under the hamburger
(Hibernate only where Windows has it enabled); they can be turned off in Settings too.

![The expanded rail](docs/screenshots/rail.png)

### Calendar
A three-month calendar with a note per day. Days with a note take their **category** colour,
and you can add, rename, recolour and delete categories. Notes import and export as JSON or
iCalendar (`.ics`). The window is resizable and remembers its size.

![Calendar](docs/screenshots/calendar.png)

### Settings

![Settings](docs/screenshots/settings.png)

**Export…** writes everything to one file — the tile layout (groups, tile sizes and positions), the
programs, files and websites you added, the menu size and list width, and every setting — and
**Import…** restores it, on this PC or another one.

## How it replaces the Start menu — and why that is safe

Winly Start never injects into `explorer.exe`, never patches files, never writes outside
`HKCU` — the one thing outside it is the optional **Start with Windows** sign-in task, registered
for your user alone in Task Scheduler so the menu is up before the desktop's delayed startup apps,
and removed when you turn it off or uninstall. It uses three ordinary, revertible user-mode
mechanisms:

* **Windows key / Ctrl+Esc** — a low-level keyboard hook. When you tap the Windows key,
  Winly Start lets the key through *and* sends a harmless masking key alongside it, so
  Windows sees a "Win + something" chord and does not open its own menu. Every Win+X
  shortcut keeps working natively.
* **Start button** — a low-level mouse hook that recognises a left-click on a taskbar's
  Start button (located through UI Automation, so it works with left- and centre-aligned
  taskbars, on every monitor that shows a taskbar). The menu opens **where your taskbar puts Start**:
  in the middle of the screen on a centred taskbar, like the Windows 11 menu, and at the corner on a
  left-aligned one, like Windows 10 — on the monitor whose Start button you clicked or, for the
  Windows key, the one the mouse pointer is on. The click counts only when the taskbar itself is
  under the pointer, so a full-screen window covering it (a remote desktop, a game) keeps the click.
* **Fallback** — if the Windows 11 menu still appears (touch gesture, or the Windows key
  pressed while an elevated app has focus, which hooks cannot see), Winly Start closes it and
  opens in its place. This works with both Start menu hosts Windows 11 has used
  (`StartMenuExperienceHost.exe`, and `SearchHost.exe` on 25H2) and is the "Keep the Windows 11
  Start menu closed" switch in Settings.

Closing Winly Start removes the hooks; Windows 11 is untouched.

## Memory

Winly Start is written to stay small: plain WPF, no UI frameworks, no WinForms, icons loaded
lazily at the exact sizes shown and frozen, a virtualised app list, live-tile timers that run
only while the menu is open, and the working set is trimmed when the menu hides.

## Build from source

```
git clone https://github.com/Mahi-BD/WinlyStart
cd WinlyStart
dotnet build src/WinlyStart -c Release
dotnet publish src/WinlyStart -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out/sc
```

Requires the .NET 8 SDK. The project sets `EnableWindowsTargeting`, so it also *compiles*
on Linux/macOS (handy for CI and reviews); it only *runs* on Windows 10/11. The installer is
built with [Inno Setup](https://jrsoftware.org/isinfo.php) from `installer/WinlyStart.iss`.

Developer notes, architecture and the project rules are in
[`Repo/Instruction/read.md`](Repo/Instruction/read.md).

## Known limitations

* Acrylic renders as a solid tint on current Windows 11 builds — the old blur API no longer
  blurs, and real blur needs the DWM system-backdrop path.
* Website live tiles show the preview image a site publishes (`og:image`), not a live browser
  render — embedding a browser engine would cost a large dependency and a lot of memory.
* Pin to taskbar and jump lists are not available (no public API).
* While an **elevated** window has focus, the Windows key is handled by the fallback path
  (brief flicker of the Windows 11 menu). Winly Start deliberately does not run elevated.
* In a **full-screen Remote Desktop or virtual-machine session** the Windows key, Ctrl+Esc and the
  Start button belong to the remote machine, exactly as they do without Winly Start — its Start menu
  opens, the local one stays out of the way. A Start-button click counts as yours only when the
  taskbar itself is under the pointer, whatever window happens to have the focus. In a windowed
  session they open Winly Start.

## Code signing

Release binaries are built on a clean GitHub Actions runner from the tagged public commit, never
from a developer machine, and every executable and the installer are signed and then verified with
`signtool verify /pa` before the release is published. Who may author, review and approve a signing
request is set out in the
[code signing policy](https://mahi-bd.github.io/WinlyStart/code-signing-policy.html).

## Privacy

Winly Start collects nothing — no telemetry, no analytics, no accounts. Everything stays in
`%LocalAppData%\WinlyStart`. The only network requests it ever makes are to a website you add as a
tile, to read that site's icon and preview image. See [the privacy policy](https://mahi-bd.github.io/WinlyStart/privacy-policy.html).

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). The two rules
that matter most: keep the code and the working set small, and never do anything that could
harm the operating system.

## License

[MIT](LICENSE). Windows, Windows 10 and Windows 11 are trademarks of Microsoft Corporation;
Winly Start is an independent project and is not affiliated with Microsoft.

<!-- SVN-REPOSITORY-NOTE -->
## Source control (SVN)

This folder is an SVN working copy of **https://rbd.svn.bd/svn/WinlyStart** (SVN user: `mahi`).

**Next time: after making changes, upload (commit) them to this repository:**

```bash
svn status                         # review changes first
svn add --force . --depth infinity # add new files (skip bin/, obj/, node_modules/, published/, secrets, databases)
svn commit -m "describe the change"
```
