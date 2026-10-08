# Launcher Updates

This launcher is configured to use GitHub Releases from:

- `https://github.com/Dwemer-Dynamics/DwemerDistro-Launcher`

The WPF app checks the latest GitHub release, downloads `DwemerDistro-win-x64.zip`, and hands the update off to `DwemerDistroUpdater.exe`.

## What updates

- `DwemerDistro.exe`
- `DwemerDistroUpdater.exe`
- any other launcher-side files shipped in the release zip

## What does not update

- the installed `DwemerAI4Skyrim3` WSL distro
- the large distro payload
- user data inside the WSL instance

## User flow after distribution

1. Install the launcher and updater together.
2. Launch `DwemerDistro.exe`.
3. The launcher checks GitHub Releases.
4. If a newer launcher exists, the bottom `Launcher` section shows an update is available.
5. Clicking `Update Launcher` downloads the new launcher zip.
6. The launcher starts `DwemerDistroUpdater.exe` from a temp location and exits.
7. The updater replaces launcher files and restarts `DwemerDistro.exe`.

## Release flow

Push a tag like:

```powershell
git tag v2.5.3
git push origin v2.5.3
```

The GitHub Actions workflow will:

1. publish the WPF app
2. publish `DwemerDistroUpdater.exe`
3. zip the release payload as `DwemerDistro-win-x64.zip`
4. build `DwemerDistroInstaller.exe` from that exact zip and the pinned base distro payload
5. write `SHA256SUMS` for the installer and the zip
6. publish a GitHub release with the installer, the zip, and `SHA256SUMS`

That release zip becomes the update payload the installed launcher reads. The installer is the first-install download; the zip is only for launchers that are already installed.

Every check runs before the release step, so a failed check publishes nothing.

Running the workflow manually (`workflow_dispatch`) performs the same build and checks for a version that must match the source, but never publishes a release. It uploads `SHA256SUMS` and the zip as workflow artifacts kept for 3 days, plus the installer when `include_installer` is set.

## Installer base payload

The installer is built by `scripts/Build-Installer.ps1` from `Dwemer-Dynamics/DwemerDistro-Installer`, checked out at the commit pinned in `INSTALLER_SOURCE_COMMIT` in `.github/workflows/release.yml`. That script validates the clean, server-free distro payload (manifest hash, absent game servers, fresh identities, no Git auth, portable pgvector).

The distro itself comes from the public base payload archive in `BASE_PAYLOAD_URL`:

- `distro-payload-full.tar.gz`, verified against `BASE_PAYLOAD_SHA256` before it is listed or extracted
- it must contain exactly the same resources as the local full installer payload: `DwemerAI4Skyrim3.tar`, `DwemerAI4Skyrim3.tar.manifest.json`, `Misc/ddistro.ico`, `README.txt`, `Manual Start Server.bat`, `Manual Update Server.bat`, and the 34 convenience scripts under `Tools/` (including `Tools/Components`, `Tools/Logs`, and `Tools/Utils`); the workflow pins every allowed path, so any missing, extra, or traversal entry fails the build
- the extracted `DwemerAI4Skyrim3.tar` is verified against `DISTRO_TAR_SHA256`

To ship a new base distro, upload a new sanitized archive and update `BASE_PAYLOAD_URL`, `BASE_PAYLOAD_SHA256`, and `DISTRO_TAR_SHA256` together. Update `INSTALLER_SOURCE_COMMIT` only to a reviewed installer commit. The distro tar must stay at or below 4,200,000,000 bytes and the finished installer below 2 GiB; the workflow fails instead of publishing a multi-file (disk-spanning) or oversized installer.
