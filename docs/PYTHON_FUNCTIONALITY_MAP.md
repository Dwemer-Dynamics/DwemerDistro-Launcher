# Python Launcher Functionality Map

Source: `../DwemerDistro-Launcher/chim_launcher.py`

Current launcher version constant: `CHIM_LAUNCHER_VERSION = "2.5.1.0"`

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
| Server Controls | `ServerControlsViewModel` |
| Updater | `UpdateViewModel` |
| Server Configuration | `ConfigurationViewModel` |
| External Links | simple commands in main view model |
| Install Components menu | `ComponentsViewModel` |
| Debugging menu | `DiagnosticsViewModel` |
| Rollback menu | `RollbackViewModel` |
| CUDA config menu | `GpuConfigViewModel` |
| Update settings menu | `UpdateSettingsViewModel` |

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
| PockeTTS | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/pocket-tts/ddistro_install.sh` |

## Configuration

| Behavior | Current storage/command |
| --- | --- |
| Configure installed components | `wsl -d DwemerAI4Skyrim3 -u dwemer -- /usr/local/bin/conf_services` |
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
| PockeTTS log | `/home/dwemer/pocket-tts/log.txt` |
| Local Whisper log | `/home/dwemer/remote-faster-whisper/log.txt` |
| Piper log | `/home/dwemer/piper/log.txt` |
| Parakeet log | `/home/dwemer/parakeet-api-server/log.txt` |
| Apache error log | `/var/log/apache2/error.log` |
| Clean logs | rotate selected files under `\\wsl.localhost\DwemerAI4Skyrim3\var\log` and HerikaServer logs |

## Launcher Auto-Update Gap

The Python launcher has a launcher version constant but no full self-update flow for replacing `DwemerDistro.exe`. The WPF rewrite should add this as a first-class launcher-only update channel using a helper updater executable.
