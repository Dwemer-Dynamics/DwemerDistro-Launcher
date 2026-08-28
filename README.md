# DwemerDistro Launcher WPF

This workspace is the C# WPF launcher rewrite for DwemerDistro.

It keeps WSL distro management in the launcher, but separates launcher binary updates from WSL distro updates. The launcher updates itself from GitHub Releases using a helper updater executable.

- `https://github.com/Dwemer-Dynamics/DwemerDistro-Launcher`

## Scope

- Native Windows launcher built with WPF on .NET 8
- install, repair, update, uninstall, and reinstall CHIM, Stobe, and Dialectic independently
- Quickstart mod selection for fresh server-free distro installs
- launcher-only self-update via `DwemerDistroUpdater.exe`
- WSL distro/server updates remain separate from launcher binary updates

## Optional Game Servers

Fresh DwemerDistro packages contain no game servers. The Quickstart **Choose Your Mods** step installs any selected products from their Main branch; selecting none is valid. The Mods page then shows each server as Not installed, Installed, or Needs repair and exposes only the actions valid for that state.

Uninstall is destructive. It requires the product-specific `PURGE-*` confirmation and removes that server's files and PostgreSQL database. It does not remove shared voice/components or another game server.

**Update Mods** updates DwemerDistro and shared components once, then updates installed mods whose Updates checkbox is enabled. Each mod's own **Update** button also updates the system first, then that checked mod on its selected branch. Neither action installs missing mods or updates unchecked mods. If the system update fails, no mod updates run. A failed mod update does not stop the remaining selected mods.

**Update System** on the Components page and **Update Distro** in Quickstart update only DwemerDistro and shared services. The launcher executable's own update remains separate.

## Workspace

- `src/DwemerDistro.Launcher.Wpf` - WPF application
- `docs/MIGRATION_PLAN.md` - phased rewrite plan
- `docs/PYTHON_FUNCTIONALITY_MAP.md` - feature inventory from the Python launcher
- `docs/PORTING_CHECKLIST.md` - current parity tracking
- `docs/UPDATES.md` - launcher release/update flow

## Default Port Map

These are the current DwemerDistro defaults. Most users do not need to open or configure these ports manually. A port only listens when its component is installed and running, and WSL may make the same service available through Windows `localhost`.

### Local Voice and AI Services

| Port | Service | Purpose |
| ---: | --- | --- |
| `8020` | XTTS | Default local XTTS API. This is also the compatibility port for older Chatterbox or Python PocketTTS installations. |
| `8021` | OmniVoice | Optional local OmniVoice TTS API. |
| `8022` | Parakeet | Local speech-to-text API. |
| `8023` | Chatterbox | Dedicated port used by current Chatterbox installations. |
| `8024` | PocketTTS (Python) | Dedicated CPU/AMD PocketTTS API used by current installations. |
| `8082` | Minime/TXT2VEC | Local text embedding and vector service. |
| `8084` | MeloTTS | Optional local MeloTTS API. |
| `8086` | PocketTTS (audio.cpp) | NVIDIA/CUDA PocketTTS API used by the recommended GPU quick setup. |

Cartesia and Inworld are cloud TTS providers, so they do not reserve a local DwemerDistro port.

Fresh installs give each local TTS service its own port. Existing Chatterbox or Python PocketTTS installations can continue using legacy port `8020`; the launcher checks the provider identity instead of assuming that every service on `8020` is XTTS. Reinstall that TTS component and re-apply its connector in TTS Studio only if you want to move it to its dedicated port.

### Game Servers and Launcher Routing

| Port | Service | Purpose |
| ---: | --- | --- |
| `7135` | Launcher discovery | Tells supported game clients which WSL server address and port to use. |
| `7513` | Skyrim TCP proxy | Windows loopback proxy to the CHIM server on port `8081`. |
| `8081` | CHIM / Skyrim | Canonical CHIM web and API server. |
| `8083` | Stobe / Kenshi | Canonical Stobe web and API server. |
| `8085` | Legacy/development Apache listener | Kept by some existing distro images for compatibility; the launcher does not use it as Dialectic's canonical URL. |
| `8087` | Starfield | Reserved for the Starfield server. |
| `8088` | Dialectic / Fallout New Vegas | Canonical Dialectic web and API server. |
| `8089` | Reign | Reserved for the Reign server. |

### Internal Runtime Services

| Port | Service | Purpose |
| ---: | --- | --- |
| `5432` | PostgreSQL | Shared local database used by the server applications. |
| `12345` | CHIM background listener | Internal CHIM background-service communication and heartbeat. |
| `12346` | Stobe background listener | Internal Stobe background-service communication and heartbeat. |
| `12347` | Dialectic background listener | Internal Dialectic background-service communication and heartbeat. |

The older `NetPortRedirection.ps1` helper only lists a subset of ports and is not the authoritative map for current launcher installs.

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
