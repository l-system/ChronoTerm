# ChronoTerm

A GPU-rendered, fully configurable terminal emulator for Linux, built on
[Silk.NET](https://github.com/dotnet/Silk.NET) (OpenGL + GLFW) and .NET.

Every visual aspect is configurable from within the app itself — no need to
hand-edit YAML unless you want to: fonts (with a searchable system-font
browser), colors (with per-channel alpha), cursor style, window size and
padding, and a set of CRT-style post effects (glow, vignette, scanlines,
screen curvature, phosphor burn-in).

## Features

- GPU-rendered text via a custom OpenGL glyph atlas — smooth even with glow
  and blur effects active
- Live in-app settings panel (right-click anywhere in the window) — drag
  sliders, toggle switches, browse fonts and cursor shapes from a searchable
  list, no restart needed for almost everything
- Named config presets — save your current look as a preset, switch between
  presets from inside the app
- CRT effects: glow, vignette, scanlines, curvature, phosphor burn-in
- Config auto-heals itself if hand-edited into an invalid state (missing
  font, corrupted colors, etc.) rather than crashing on startup

## Requirements

- A Linux system with an OpenGL 3.3+ capable GPU/driver
- [`fontconfig`](https://www.freedesktop.org/wiki/Software/fontconfig/) — used
  to discover installed fonts (falls back to walking common font directories
  if unavailable)
- `setsid` (part of `util-linux`, present on essentially every Linux
  install) — used to give the spawned shell a proper controlling terminal
- GLFW (`libglfw.so.3`) — either installed system-wide (the AUR package
  depends on it) or shipped alongside a self-contained build (see below)

## Installing

### Arch Linux (AUR)

```bash
paru -S chronoterm
# or
yay -S chronoterm
```

See [`aur/PKGBUILD`](aur/PKGBUILD) if you'd rather build it yourself without
an AUR helper.

### Building from source

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (the version
in [`global.json`](global.json) or newer).

```bash
git clone https://github.com/<you>/chronoterm.git
cd chronoterm
dotnet build -c Release
dotnet run -c Release --project ChronoTerm
```

### Publishing a standalone binary

```bash
dotnet publish ChronoTerm -c Release -r linux-x64 --self-contained true
```

Produces exactly two files in `ChronoTerm/bin/Release/net10.0/linux-x64/publish/`:
`ChronoTerm` (the self-contained executable, debug symbols embedded) and
`libglfw.so.3`. Both are required — `libglfw.so.3` can't be folded into the
single-file bundle because Silk.NET's GLFW loader does its own native-library
search (including checking the executable's own directory) rather than going
through the standard single-file extraction path. Ship them together, always
in the same directory.

## Configuration

- Active config: `~/.config/ChronoTerm/config.yaml` (created with sane
  defaults on first run)
- Saved presets: `~/.config/ChronoTerm/configs/*.yaml` — created via the
  settings panel's "Save As", loaded via "Load"

You very likely never need to hand-edit these — everything they contain is
reachable from the in-app settings panel — but they're plain YAML if you want
to script or version-control your setup.

## License

[MIT](LICENSE). Dependencies: Silk.NET (MIT), SixLabors.Fonts/ImageSharp
(Apache 2.0, pinned to the last versions before their license-key
requirement — see the comment in `ChronoTerm.csproj`), YamlDotNet (MIT).
