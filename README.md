# DwemerDistro Launcher WPF

This workspace is the C# WPF launcher rewrite for DwemerDistro.

It keeps WSL distro management in the launcher, but separates launcher binary updates from WSL distro updates. The launcher updates itself from GitHub Releases using a helper updater executable.

- `https://github.com/Dwemer-Dynamics/DwemerDistro-Launcher`

## Scope

- Native Windows launcher built with WPF on .NET 8
- 1:1 port of the current Python launcher behavior
- launcher-only self-update via `DwemerDistroUpdater.exe`
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
- `dist\win-x64\DwemerDistroUpdater.exe` once the updater project is copied beside it for packaging

## Release Updates

Push a git tag like `v2.5.3`.

The GitHub Actions workflow in `.github/workflows/release.yml` will:

1. publish the launcher
2. publish the updater helper
3. zip both executables into `DwemerDistro-win-x64.zip`
4. upload that zip to GitHub Releases

Installed launchers then detect updates from that repo in the bottom `Launcher` section of the app.
