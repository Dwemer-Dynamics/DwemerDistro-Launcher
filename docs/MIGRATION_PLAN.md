# WPF + Velopack Migration Plan

## Product Boundary

There are two release/update channels:

1. Initial installer:
   - Bundles `DwemerAI4Skyrim3.tar`.
   - Installs/checks WSL2.
   - Imports the distro as `DwemerAI4Skyrim3`.
   - Installs the Windows launcher.
   - Creates the desktop shortcut.

2. Launcher self-update:
   - Updates the WPF launcher executable and launcher support files only.
   - Does not redownload or re-import the WSL tar.
   - Leaves distro/server updates to the existing WSL/git update commands.

## Recommended Architecture

- `DwemerDistro.Launcher.Wpf`
  - WPF views, windows, dialogs, and user-facing state.
  - Thin event handlers that call services.

- Future `DwemerDistro.Launcher.Core`
  - WSL command runner.
  - Server lifecycle service.
  - Update service.
  - Component installer service.
  - Diagnostics/log collection service.
  - Settings service.
  - TCP proxy and discovery service.

- Future `packaging/`
  - Velopack packaging scripts.
  - First-install bootstrap installer scripts.
  - Release manifest/build notes.

## Key Design Rules

- Keep all WSL command strings centralized in one service.
- Stream process output into the UI log instead of waiting for command completion.
- Use cancellation tokens for long-running WSL processes.
- Treat launcher updates and distro/server updates as separate buttons/flows.
- Store launcher settings under `%LOCALAPPDATA%\DwemerDynamics\DwemerDistroLauncher`.
- Store WSL-backed settings in the same flag files used by the Python launcher until the server side changes.
- Keep `DwemerAI4Skyrim3` as the distro name unless we deliberately add multi-distro support.

## Phase 1 - Parity Skeleton

- Build the main WPF shell:
  - Server Controls
  - Updater
  - Server Configuration
  - External Links
  - Output log panel
- Add a `WslCommandRunner` that supports:
  - hidden process execution
  - stdout/stderr streaming
  - exit-code capture
  - cancellation
- Add start/stop/force stop:
  - `wsl -d DwemerAI4Skyrim3 -- /etc/start_env`
  - `wsl -t DwemerAI4Skyrim3`
- Add WSL IP detection:
  - `wsl -d DwemerAI4Skyrim3 hostname -I`
- Port TCP proxy and HTTP discovery:
  - proxy `127.0.0.1:7513` to WSL port `8081`
  - discovery server on `7135`

## Phase 2 - Distro and Server Updates

- Port the current "Update" flow:
  - switch selected HerikaServer/StobeServer branches
  - run distro update in `/home/dwemer/dwemerdistro`
  - run `/usr/local/bin/update_gws`
  - support skip flags for disabled HerikaServer/StobeServer updates
- Port update status checks:
  - HerikaServer branch/version
  - StobeServer branch/version
  - dwemerdistro `.version.txt`
  - Nexus version labels/open links
- Preserve rollback safeguards:
  - detect detached HEAD
  - switch back to tracked branch before update
  - expose rollback menu later if needed

## Phase 3 - Configuration and Components

- Port folder open actions:
  - `\\wsl.localhost\DwemerAI4Skyrim3\var\www\html`
  - `\\wsl.localhost\DwemerAI4Skyrim3\home\dwemer\piper\voices`
- Port component installers:
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
- Port settings:
  - MCP enabled flag
  - update include flags
  - target branch selections
  - CUDA GPU selection

## Phase 4 - Debugging and Diagnostics

- Port debugging tools:
  - open terminal
  - fix WSL DNS
  - memory usage
  - service log tails
  - Apache error logs
  - clean logs
- Port diagnostics bundle generation:
  - WSL command output
  - local/WSL log tails
  - selected version and branch state

## Phase 5 - Launcher Self-Update

- Add Velopack packaging after parity shell is stable.
- Use launcher version from the .NET assembly version.
- On startup:
  - initialize Velopack early in app startup.
  - check the launcher update feed if enabled.
  - show an update prompt or background download status.
- On update:
  - download launcher package only.
  - apply via Velopack.
  - restart launcher.
- Keep WSL distro/server update in the existing "Update" UI flow.

## Phase 6 - Initial Installer

- Build a first-install package that includes:
  - WPF launcher build output.
  - `DwemerAI4Skyrim3.tar`.
  - installer/bootstrap script.
  - icon and desktop shortcut metadata.
- Installer behavior:
  - check WSL availability.
  - enable required Windows features when elevated, or clearly tell the user what to enable.
  - run `wsl --update`.
  - import `DwemerAI4Skyrim3.tar` as `DwemerAI4Skyrim3`.
  - create launcher desktop shortcut.
  - optionally delete or retain the tar after successful import.

## Acceptance Criteria Before Replacing Python Launcher

- Starts the installed WSL server and detects readiness.
- Stops and force-stops the WSL distro.
- Keeps the Skyrim proxy/discovery behavior working.
- Runs a full distro/server update with the same branch options.
- Installs and configures optional components.
- Produces useful diagnostics.
- Can self-update the launcher from a hosted test feed.
- Does not require redistributing the 3.7GB WSL tar for launcher-only changes.

