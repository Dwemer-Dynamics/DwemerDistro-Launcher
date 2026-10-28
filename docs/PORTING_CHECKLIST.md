# WPF Launcher Porting Checklist (Historical)

This is a historical record of how the retired Python launcher (`chim_launcher.py`, version 2.5.1.0) was ported to the WPF launcher. The port was completed for the WPF 3.3.0 release. The text below has been refreshed while preparing launcher 3.4.0 so that it no longer misdescribes the current source; it is not a release note.

## Current Layout (From Source)

The current source (`src/DwemerDistro.Launcher.Wpf/MainWindow.xaml`) differs from the 3.3.0 layout:

- Six top-level destinations: **Mods**, **Components**, **Settings**, **API Keys**, **Assistant**, and **Logs**.
- Persistent header actions: **Update Distro**, **Start Server**, and **Force Stop**.
- Left sidebar on **Mods**: **Official mods** (CHIM, LORKHAN, DIALECTIC, STOBE, REIGN) and **Custom mods** lists, plus Server Folder, Terminal, Wiki, Discord, and Patreon links.
- Per-mod controls on each mod card: webpage, update branch, an **Update** button, logs, rollback, and uninstall.
- Launcher self-update button in the bottom-left corner (**Check Update** / **Up To Date**).
- **Show Console** panel at the bottom.

## Ported From Python (Historical First Pass)

Items are kept for reference. Where the current source behaves differently, the current behavior is noted.

- Main window shell:
  - Python Server Controls, Updater, Server Configuration, and External Links groups. These are now spread across the header actions, mod cards, sidebar, and top-level pages listed above.
  - Python output log panel. This is now the **Show Console** / **Hide Console** panel.
- Server lifecycle:
  - start server with `wsl -d DwemerAI4Skyrim3 -- /etc/start_env`
  - detect readiness from `AIAgent.ini Network Settings:`
  - stop server by sending newline and terminating WSL
  - force stop WSL distro
  - WSL IP detection with `hostname -I`
- Runtime bridge services:
  - local TCP proxy on `127.0.0.1:7513` (`TcpProxyService`)
  - LORKHAN loopback proxy on `127.0.0.1:7514`
  - discovery service on port `7135` (`DiscoveryService`)
  - `GET /discover?game=skyrim` (default) returns WSL IP port `8081`
  - `GET /discover?game=kenshi` or `game=stobe` returns WSL IP port `8083`
  - current source also maps `starfield` to `8087`, `dialectic`/`fallout`/`fnv`/`newvegas` to `8088`, and `reign` to `8089`, and returns `127.0.0.1:7514` for `lorkhan`/`morrowind`/`openmw`
- Updater:
  - Python branch selectors were `aiagent`/`dev` (HerikaServer) and `stobe`/`dev` (StobeServer).
  - Current source offers **Main** and **Dev** for CHIM (HerikaServer), STOBE, and DIALECTIC, and **Dev** only for REIGN and LORKHAN. The launcher sends `main`, `dev`, `reign`, or `unstable` to `/usr/local/bin/ddistro_server`, and DwemerDistro-Core maps **Main** to each server's production branch (for example `aiagent`, `stobe`, `dialectic`).
  - Server install, update, repair, and uninstall run through `/usr/local/bin/ddistro_server`.
  - Python persisted update include flags under `/home/dwemer`. Current update preferences live launcher-side in `%LOCALAPPDATA%\DwemerDistro\update-preferences.json` (`UpdatePreferencesService`).
  - distro update command
  - **Update Distro** runs `update.sh`, then `/usr/local/bin/update_gws` with every server skip flag (`--skip-herika --skip-stobe --skip-dialectic --skip-reign --skip-lorkhan`), so only shared services update there.
  - version status checks for HerikaServer, StobeServer, DialecticServer, ReignServer, and LorkhanServer
  - Python Nexus version label. Current source links to the CHIM, STOBE, and DIALECTIC Nexus pages but does not scrape Nexus versions.
- Server configuration:
  - open server folder
  - install and configure components from the **Components** page
  - MCP enable flag load/save (`/home/dwemer/.mcp_enabled`)
- Component installer commands (the current list is in `Resources/dwemerdistro-components.json`):
  - CUDA/full packages
  - XTTS
  - Chatterbox
  - MeloTTS
  - Minime-T5
  - Mimic3
  - Piper-TTS
  - Local Whisper
  - Parakeet
  - Pocket-TTS
  - Piper voices folder
  - added after the Python launcher: LLM Studio, CHIM-MCP, Higgs TTS 3, Multilingual OmniVoice TTS, Text2Vec, llama.cpp, and Dwemer Dashboard
- Settings and Logs commands:
  - open terminal
  - view memory usage
  - view service logs
  - view Apache logs
  - fix WSL DNS
  - clean logs
  - **Generate Distro Debugging Logs** diagnostic bundle on the **Logs** page
  - Distro Doctor
- Product rollback actions on the mod cards.
- CUDA configuration from the **Components** page.
- GitHub release detection, updater helper, package download, apply, and restart.

## Shipped Outside The Launcher

These Python-era items are already handled by the DwemerDistro Installer (`Dwemer-Dynamics/DwemerDistro-Installer`). They are no longer porting work for this repo:

- WSL2 preparation (`wsl --install --no-distribution`, with a Windows feature fallback)
- WSL import of `DwemerAI4Skyrim3.tar`
- desktop shortcut creation
- uninstall behavior, including distro unregister

The launcher also has its own **Settings** actions to export and import the distro.

## Not Ported (Remaining Parity Gaps)

- Full ANSI color rendering in the WPF log panel. The current source strips ANSI sequences.
- Clickable URLs in the log panel.
- Structured save-location prompt for diagnostics. The WPF bundle collects bounded log tails and system details and saves them to `Desktop\DwemerDistro-Diagnostics` without asking where to save.
- Python's Nexus scraping fallbacks. The current source does not read versions from Nexus.

## Historical Validation Notes

- Release builds and regression guards (`tests/DwemerDistro.Launcher.RegressionTests`) validate the WPF launcher and updater.
- Local release checks run against an installed `DwemerAI4Skyrim3` distro before publication.
- Game-specific behavior still requires the matching game/plugin validation when those paths change.
