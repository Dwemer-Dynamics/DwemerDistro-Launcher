# Python Launcher Functionality Map

Source: `../DwemerDistro-Launcher/chim_launcher.py`

Current launcher version constant: `CHIM_LAUNCHER_VERSION = "2.5.1.0"`

This is a historical map from the retired Python interface to the released WPF launcher. In WPF launcher 3.3.0, the main workflow is organized under the top-level **MODS**, **COMPONENTS**, **SETTINGS**, and **LOGS** pages.

## App Services

| Python area | WPF/C# target |
| --- | --- |
| `SimpleTCPProxy` | `TcpProxyService` |
| `DiscoveryHTTPServer` | `DiscoveryService` |
| `append_output`, URL detection, ANSI cleanup | `LogViewModel` + `AnsiLogFormatter` |
| `run_command_in_new_window` | `ExternalTerminalService` |
| `run_wsl_bash_capture` | `WslCommandRunner` |

## Main Window UI

| Python UI group | WPF view model area |
| --- | --- |
| Server Controls | persistent top action bar in `MainWindowViewModel` |
| Updater | **MODS** page and `MainWindowViewModel` |
| Server Configuration | **SETTINGS** and **COMPONENTS** pages |
| External Links | left sidebar commands in `MainWindowViewModel` |
| Install Components menu | top-level **COMPONENTS** page |
| Debugging menu | **SETTINGS** tools and top-level **LOGS** page |
| Rollback menu | product rollback actions on the **MODS** page |
| CUDA config menu | **Configure CUDA** on the **COMPONENTS** page |
| Update settings menu | branch selectors and per-mod **Updates** checkboxes on the **MODS** page |

## Server Lifecycle

| Behavior | Current command/trigger |
| --- | --- |
| Start server | `wsl -d DwemerAI4Skyrim3 -- /etc/start_env` |
| Ready detection | output contains `AIAgent.ini Network Settings:` |
| Stop server | send newline to process stdin, then `wsl -t DwemerAI4Skyrim3` |
| Force stop | `wsl -t DwemerAI4Skyrim3` and kill tracked process if needed |
| Get WSL IP | `wsl -d DwemerAI4Skyrim3 hostname -I` |
| Proxy target | WSL IP port `8081` |
| Local proxy listen | `127.0.0.1:7513` |
| Discovery listen | port `7135` |

## Distro and Server Updates

| Behavior | Current command/logic |
| --- | --- |
| Distro repo bootstrap | create `/home/dwemer/dwemerdistro`, clone `https://github.com/abeiro/dwemerdistro.git` if missing |
| Distro repo update | `cd /home/dwemer/dwemerdistro && git fetch origin && git reset --hard origin/main` |
| Distro apply | `chmod +x update.sh && echo 'dwemer' \| sudo -S ./update.sh` |
| HerikaServer update | `/usr/local/bin/update_gws` |
| Full update | distro update, then `/usr/local/bin/update_gws` with optional skip flags |
| Skip HerikaServer | `/usr/local/bin/update_gws --skip-herika` |
| Skip StobeServer | `/usr/local/bin/update_gws --skip-stobe` |
| StobeServer repo bootstrap | clone `https://github.com/Dwemer-Dynamics/StobeServer.git` |
| HerikaServer target branches | `aiagent`, `dev` |
| StobeServer target branches | `stobe`, `dev` |

## Version Checks

| Behavior | Current source |
| --- | --- |
| Distro latest version | `https://raw.githubusercontent.com/abeiro/dwemerdistro/main/.version.txt` |
| Distro local version | `/home/dwemer/dwemerdistro/.version.txt` or `/etc/.version.txt` |
| HerikaServer local date version | `\\wsl$\DwemerAI4Skyrim3\var\www\html\HerikaServer\.version.txt` |
| HerikaServer local semantic version | `\\wsl$\DwemerAI4Skyrim3\var\www\html\HerikaServer\.version_number.txt` |
| StobeServer local date version | `\\wsl$\DwemerAI4Skyrim3\var\www\html\StobeServer\.version.txt` |
| StobeServer local semantic version | `.version_number.txt` or `versionnumber.txt` |
| CHIM Nexus page | `https://www.nexusmods.com/skyrimspecialedition/mods/126330` |
| STOBE Nexus page | `https://www.nexusmods.com/kenshi/mods/1891?tab=description` |

## Component Installers

| UI action | Current command |
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
| Pocket-TTS (GPU / audio.cpp) | NVIDIA Quickstart runs `/usr/local/bin/install_audiocpp_pockettts 12.8` and enables `/home/dwemer/audio.cpp/start.sh` on port `8086`. |
| Pocket-TTS (CPU / Python) | AMD / CPU Quickstart installs `/home/dwemer/pocket-tts` and enables its CPU start script on dedicated port `8024`. |

## Configuration

| Behavior | Current storage/command |
| --- | --- |
| Configure a component from the **COMPONENTS** page | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /usr/local/bin/conf_services` |
| Open server folder | `\\wsl.localhost\DwemerAI4Skyrim3\var\www\html` |
| Open Piper voices folder | `\\wsl.localhost\DwemerAI4Skyrim3\home\dwemer\piper\voices` |
| MCP flag | `/home/dwemer/.mcp_enabled` |
| Update include flags | WSL flag files written as root by Python launcher |
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
| Clean logs | rotate selected files under `\\wsl.localhost\DwemerAI4Skyrim3\var\log` and HerikaServer logs |

## Launcher Auto-Update

The Python launcher had only a version constant. The released WPF launcher now checks GitHub Releases, downloads the launcher package, starts the helper updater, replaces the launcher files, and restarts into the new version. The launcher-only update control remains in the bottom-left corner and reads **Up To Date** when no release is waiting.
