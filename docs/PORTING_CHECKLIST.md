# 1:1 Launcher Porting Checklist

This checklist tracks the Python launcher features as they move into the WPF launcher. "First pass" means the feature is wired in WPF with the same command or behavioral path, but may still need UX polish and runtime validation against an installed distro.

## First Pass Ported

- Main window shell:
  - Server Controls
  - Updater
  - Server Configuration
  - External Links
  - output log panel
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
  - HerikaServer branch selector: `aiagent`, `dev`
  - StobeServer branch selector: `stobe`, `dev`
  - include/exclude toggles for HerikaServer and StobeServer
  - persisted update include flags under `/home/dwemer`
  - branch switching before update
  - StobeServer repo bootstrap/migration guard
  - distro update command
  - `/usr/local/bin/update_gws` with skip flags
  - version status checks for HerikaServer and StobeServer
  - Nexus version label first pass
- Server configuration:
  - open server folder
  - configure installed components
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
- Debugging commands:
  - open terminal
  - view memory usage
  - view service logs
  - view Apache logs
  - fix WSL DNS
  - clean logs first pass
  - create diagnostic file first pass

## Still To Port For Exact Parity

- Full ANSI color rendering in the WPF log panel. Current pass strips ANSI sequences.
- Clickable URLs in the log panel.
- Original image/icon usage:
  - `DwemerDistro.png`
  - `dd_title.png`
  - `nvidia.png`
  - `amd.png`
- Install Components window hover-description behavior and GPU icon display.
- Rollback UI:
  - HerikaServer rollback target list
  - StobeServer rollback target list
  - detached HEAD rollback execution
- CUDA GPU configuration modal and `/home/dwemer/.cuda_config` save behavior.
- Update Settings modal, if we still want it separate from the inline updater controls.
- More complete diagnostics bundle:
  - tails of the same log files as Python
  - local Windows file probes
  - structured save location prompt
- Python launcher's exact Nexus scraping fallbacks. Current WPF pass uses a simple version regex.
- Launcher self-update:
  - GitHub release detection
  - updater helper EXE
  - zip download/apply/restart flow
- Installer integration:
  - WSL2 feature checks
  - WSL import of `DwemerAI4Skyrim3.tar`
  - desktop shortcut creation
  - uninstall behavior

## Validation Notes

- Build currently validates the WPF code compiles.
- Runtime validation still needs to be done against an installed `DwemerAI4Skyrim3` distro.
- The WPF launcher must not replace the Python launcher until proxy, discovery, start/stop, update, and component install paths have been tested against the game/plugin clients.
