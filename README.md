# DwemerDistro Launcher WPF

This workspace is the C# WPF launcher rewrite for DwemerDistro.

It keeps WSL distro management in the launcher, but separates launcher binary updates from WSL distro updates. The launcher is wired to use Velopack with GitHub Releases from:

- `https://github.com/Dwemer-Dynamics/DwemerDistro-Launcher`

## Scope

- Native Windows launcher built with WPF on .NET 8
- 1:1 port of the current Python launcher behavior
- launcher-only self-update via Velopack
- WSL distro/server updates remain separate from launcher binary updates

## Workspace

- `src/DwemerDistro.Launcher.Wpf` - WPF application
- `docs/MIGRATION_PLAN.md` - phased rewrite plan
- `docs/PYTHON_FUNCTIONALITY_MAP.md` - feature inventory from the Python launcher
- `docs/PORTING_CHECKLIST.md` - current parity tracking
- `docs/UPDATES.md` - launcher release/update flow

## Build

```powershell
dotnet build .\DwemerDistroLauncherWpf.sln
```

## Publish EXE

```powershell
dotnet publish .\src\DwemerDistro.Launcher.Wpf\DwemerDistro.Launcher.Wpf.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\dist\win-x64
```

Published output:

- `dist\win-x64\DwemerDistro.exe`

## Release Updates

Push a git tag like `v2.5.1`.

The GitHub Actions workflow in `.github/workflows/release.yml` will:

1. publish the launcher
2. pack a Velopack release
3. upload release assets to GitHub Releases

Installed launchers then detect updates from that repo in the bottom `Launcher` section of the app.
