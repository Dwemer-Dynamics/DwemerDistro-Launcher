# Python Launcher Functionality Map (Historical)

Source: `../DwemerDistro-Launcher/chim_launcher.py` (retired Python launcher)

Python launcher version constant: `CHIM_LAUNCHER_VERSION = "2.5.1.0"`

This is a historical map from the retired Python interface to the WPF launcher. The port shipped with WPF launcher 3.3.0. The map has been refreshed while preparing launcher 3.4.0 so that its WPF targets match the current source in `src/DwemerDistro.Launcher.Wpf`. Python names and commands are kept for reference.

The current source has six top-level destinations: **Mods**, **Components**, **Settings**, **API Keys**, **Assistant**, and **Logs**.

## App Services

| Python area | Current WPF/C# source |
| --- | --- |
| `SimpleTCPProxy` | `Services/TcpProxyService.cs` |
| `DiscoveryHTTPServer` | `Services/DiscoveryService.cs` |
| `append_output`, URL detection, ANSI cleanup | `MainWindowViewModel.AppendLog` (strips ANSI sequences; no URL detection) |
| `run_command_in_new_window` | `MainWindowViewModel.RunCommandInNewWindow` via `ProcessRunner.RunInNewConsole` |
| `run_wsl_bash_capture` | `Services/WslService.cs` (`RunBashAsync`) via `Services/ProcessRunner.cs` |

## Main Window UI

| Python UI group | Current WPF location |
| --- | --- |
| Server Controls | persistent header actions (**Update Distro**, **Start Server**, **Force Stop**) |
| Updater | **Update Distro** in the header, plus per-mod branch and **Update** controls on each **Mods** card |
| Server Configuration | **Settings** and **Components** pages |
| External Links | left sidebar on **Mods** (Server Folder, Terminal, Wiki, Discord, Patreon) and each mod card's webpage link |
| Install Components menu | top-level **Components** page |
| Debugging menu | **Settings** tools and the top-level **Logs** page |
| Rollback menu | rollback action on each server's **Mods** card |
| CUDA config menu | **Configure CUDA** on the **Components** page |
| Update settings menu | per-mod branch selector on the **Mods** cards; launcher-side preferences in `%LOCALAPPDATA%\DwemerDistro\update-preferences.json` |

## Server Lifecycle

| Behavior | Current command/trigger |
| --- | --- |
| Start server | `wsl -d DwemerAI4Skyrim3 -- /etc/start_env` |
| Ready detection | output contains `AIAgent.ini Network Settings:` |
| Stop server | send newline to process stdin, then `wsl -t DwemerAI4Skyrim3` |
| Force stop | `wsl -t DwemerAI4Skyrim3` and kill tracked process if needed |
| Get WSL IP | `wsl -d DwemerAI4Skyrim3 hostname -I` |
| Proxy target | WSL IP port `8081` |
| Local proxy listen | `127.0.0.1:7513` (LORKHAN uses `127.0.0.1:7514`) |
| Discovery listen | port `7135` |

## Distro and Server Updates

| Behavior | Python command/logic | Current source |
| --- | --- | --- |
| Distro repo bootstrap | create `/home/dwemer/dwemerdistro`, clone `https://github.com/Dwemer-Dynamics/DwemerDistro-Core.git` if missing | `git init` in `/home/dwemer/dwemerdistro` if needed, then set `origin` to the Core repo |
| Distro repo update | `cd /home/dwemer/dwemerdistro && git fetch origin && git reset --hard origin/main` | same, with credential helpers disabled for the fetch |
| Distro apply | `chmod +x update.sh && echo 'dwemer' \| sudo -S ./update.sh` | same |
| Shared services update | `/usr/local/bin/update_gws` with optional skip flags | `/usr/local/bin/update_gws --skip-herika --skip-stobe --skip-dialectic --skip-reign --skip-lorkhan` (shared services only) |
| Server install, update, repair, uninstall | `update_gws`, plus StobeServer clone from `https://github.com/Dwemer-Dynamics/StobeServer.git` | `/usr/local/bin/ddistro_server <install\|update\|repair\|uninstall> <herika\|stobe\|dialectic\|reign\|lorkhan>` |
| HerikaServer target branches | `aiagent`, `dev` | **Main** / **Dev** (Core maps **Main** to `aiagent`) |
| StobeServer target branches | `stobe`, `dev` | **Main** / **Dev** (Core maps **Main** to `stobe`) |
| DialecticServer target branches | not in Python | **Main** / **Dev** (Core maps **Main** to `dialectic`) |
| ReignServer and LorkhanServer target branches | not in Python | **Dev** only in the launcher |

## Version Checks

| Behavior | Current source |
| --- | --- |
| Distro latest version | `https://raw.githubusercontent.com/Dwemer-Dynamics/DwemerDistro-Core/main/system-release.json` (Python used `.version.txt`) |
| HerikaServer local date version | `/var/www/html/HerikaServer/.version.txt` |
| HerikaServer local semantic version | `/var/www/html/HerikaServer/.version_number.txt` |
| StobeServer local date version | `/var/www/html/StobeServer/.version.txt` |
| StobeServer local semantic version | `.version_number.txt` or `versionnumber.txt` |
| DialecticServer local versions | `/var/www/html/DialecticServer/.version.txt` and `.version_number.txt` |
| ReignServer local versions | `/var/www/html/ReignServer/runtime/current/.version.txt` and `.version_number.txt` |
| Remote server date version | `https://raw.githubusercontent.com/Dwemer-Dynamics/<Server>/<branch>/.version.txt` |
| CHIM Nexus page | `https://www.nexusmods.com/skyrimspecialedition/mods/126330` |
| STOBE Nexus page | `https://www.nexusmods.com/kenshi/mods/1891` |
| DIALECTIC Nexus page | `https://www.nexusmods.com/newvegas/mods/99233` |

Python read the local files through `\\wsl$\DwemerAI4Skyrim3\...`. Current source reads them inside the distro.

## Component Installers

The current visible component list is the **Components** page cards in `InstallComponentsWindowViewModel`. `Resources/dwemerdistro-components.json` is a compatibility catalog that also tracks entries with no card. The install commands are in `MainWindowViewModel.GetComponentInstallDefinition`.

| UI action | Python-era command (the Pocket-TTS rows describe current Quickstart) |
| --- | --- |
| CUDA/full packages | `wsl -d DwemerAI4Skyrim3 -- /usr/local/bin/install_full_packages` |
| XTTS | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/xtts-api-server/ddistro_install.sh` |
| Chatterbox | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/chatterbox/ddistro_install.sh` |
| MeloTTS | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/MeloTTS/ddistro_install.sh` |
| Minime-T5 | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/minime-t5/ddistro_install.sh` |
| Mimic3 | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/mimic3/ddistro_install.sh` |
| Piper-TTS | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/piper/ddistro_install.sh` |
| Local Whisper | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/remote-faster-whisper/ddistro_install.sh` |
| Parakeet | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/parakeet-api-server/ddistro_install.sh` |
| Pocket-TTS (GPU / audio.cpp) | Current Quickstart runs `/usr/local/bin/install_audiocpp_pockettts auto` and links `/home/dwemer/audio.cpp/start.sh`; the Core installer serves it on port `8086`. |
| Pocket-TTS (CPU / Python) | AMD / CPU Quickstart installs `/home/dwemer/pocket-tts` and enables its CPU start script on dedicated port `8024`. |

Components page cards added after the Python launcher: LLM Studio, Higgs TTS 3, and Multilingual OmniVoice TTS. Python's Minime-T5 is now the Minime and TXT2VEC card, which also configures Text2Vec. The catalog also tracks CHIM-MCP, llama.cpp, and Dwemer Dashboard, which have no Components card.

## Configuration

| Behavior | Current storage/command |
| --- | --- |
| Configure components | install and configure from the **Components** page (Python used `/usr/local/bin/conf_services`) |
| Open server folder | `\\wsl.localhost\DwemerAI4Skyrim3\var\www\html` |
| Open Piper voices folder | `\\wsl.localhost\DwemerAI4Skyrim3\home\dwemer\piper\voices` |
| MCP flag (Python-only/historical; no current launcher UI) | `/home/dwemer/.mcp_enabled` |
| Update preferences | `%LOCALAPPDATA%\DwemerDistro\update-preferences.json` (Python wrote WSL flag files as root) |
| CUDA GPU setting | `/home/dwemer/.cuda_config` |

## Debugging and Diagnostics

| Behavior | Current command/path |
| --- | --- |
| Terminal | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /usr/local/bin/terminal` |
| Fix WSL DNS | write `/etc/wsl.conf` and `/etc/resolv.conf`, then `wsl --shutdown` |
| Memory usage | `wsl -d DwemerAI4Skyrim3 -- htop` |
| MeloTTS log | `/home/dwemer/MeloTTS/melo/log.txt` |
| XTTS log | `/home/dwemer/xtts-api-server/log.txt` |
| Chatterbox log | `/home/dwemer/chatterbox/log.txt` |
| Pocket-TTS audio.cpp log | `/home/dwemer/audio.cpp/server.log` |
| Pocket-TTS Python log | `/home/dwemer/pocket-tts/log.txt` |
| Local Whisper log | `/home/dwemer/remote-faster-whisper/log.txt` |
| Piper log | `/home/dwemer/piper/log.txt` |
| Parakeet log | `/home/dwemer/parakeet-api-server/log.txt` |
| Apache error log | `/var/log/apache2/error.log` |
| Clean logs | Python rotated files under `\\wsl.localhost\DwemerAI4Skyrim3\var\log`; current source renames selected Apache and HerikaServer logs to `.bak` from inside the distro |
| Diagnostic bundle | **Generate Distro Debugging Logs** on the **Logs** page, saved to `Desktop\DwemerDistro-Diagnostics` |
| Distro Doctor | **Settings** page |

## Launcher Auto-Update

The Python launcher had only a version constant. The WPF launcher checks GitHub Releases, downloads the launcher package, starts the helper updater, replaces the launcher files, and restarts into the new version. The launcher update button is in the bottom-left corner. It reads **Check Update** by default and **Up To Date** when no release is waiting. See `docs/UPDATES.md`.
