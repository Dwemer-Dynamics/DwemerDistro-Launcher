# WPF Launcher Porting Checklist

This historical checklist tracks the Python launcher features that moved into the WPF launcher. The WPF launcher is now the released interface. Version 3.3.0 places the main workflow on the top-level **MODS**, **COMPONENTS**, **SETTINGS**, and **LOGS** pages.

## First Pass Ported

- Main window shell:
  - top-level Mods, Components, Settings, and Logs navigation
  - persistent Update Mods and server controls
  - product selector with per-mod update preferences
  - Server Controls
  - Updater
  - Server Configuration
  - External Links
  - Show Console / Hide Console panel
- Server lifecycle:
  - start server with `wsl -d DwemerAI4Skyrim3 -- /etc/start_env`
  - detect readiness from `AIAgent.ini Network Settings:`
  - stop server by sending newline and terminating WSL
  - force stop WSL distro
  - WSL IP detection with `hostname -I`
- Runtime bridge services:
  - local TCP proxy on `127.0.0.1:7513`
  - discovery service on `127.0.0.1:7135`
  - `GET /discover?game=skyrim` returns WSL IP port `8081`
  - `GET /discover?game=kenshi` or `game=stobe` returns WSL IP port `8083`
- Updater:
  - `main` / `dev` branch selectors for HerikaServer, StobeServer, and DialecticServer
  - per-mod Updates checkboxes for HerikaServer, StobeServer, and DialecticServer
  - persisted update include flags under `/home/dwemer`
  - branch switching before update
  - StobeServer repo bootstrap/migration guard
  - distro update command
  - `/usr/local/bin/update_gws` with skip flags
  - version status checks for HerikaServer, StobeServer, and DialecticServer
  - Nexus version label first pass
- Server configuration:
  - open server folder
  - configure installed components from the Components page
  - MCP enable flag load/save
- Component installer commands:
  - CUDA/full packages
  - XTTS
  - Chatterbox
  - MeloTTS
  - Minime-T5
  - Mimic3
  - Piper-TTS
  - Local Whisper
  - Parakeet
  - PockeTTS
  - Piper voices folder
- Settings and Logs commands:
  - open terminal
  - view memory usage
  - view service logs
  - view Apache logs
  - fix WSL DNS
  - clean logs first pass
  - create diagnostic file first pass
- Product rollback actions on the Mods page.
- CUDA configuration from the Components page.
- Inline update controls on the Mods page.
- GitHub release detection, updater helper, package download, apply, and restart.

## Still To Port For Exact Parity

- Full ANSI color rendering in the WPF log panel. Current pass strips ANSI sequences.
- Clickable URLs in the log panel.
- More complete diagnostics bundle:
  - tails of the same log files as Python
  - local Windows file probes
  - structured save location prompt
- Python launcher's exact Nexus scraping fallbacks. Current WPF pass uses a simple version regex.
- Installer integration:
  - WSL2 feature checks
  - WSL import of `DwemerAI4Skyrim3.tar`
  - desktop shortcut creation
  - uninstall behavior

## Historical Validation Notes

- Release builds and regression guards validate the WPF launcher and updater.
- Local release checks run against an installed `DwemerAI4Skyrim3` distro before publication.
- Game-specific behavior still requires the matching game/plugin validation when those paths change.
