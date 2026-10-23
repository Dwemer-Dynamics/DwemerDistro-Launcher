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

Release CI has been removed. Build candidates locally without creating a tag or
release. Follow [Assistant release preparation](ASSISTANT_RELEASE.md) for the
coordinated Core, Launcher and Installer checks.

After separate publication approval:

1. Review and merge the coordinated dependency PRs.
2. Choose a fresh version and synchronize the project, `LauncherConstants` and regression assertion.
3. Run regression checks and publish the launcher and updater as self-contained win-x64 applications.
4. Package that exact output as `DwemerDistro-win-x64.zip`.
5. Build `DwemerDistroInstaller.exe` from the exact ZIP and verified clean payload using the reviewed Installer commit.
6. Record source commits and SHA256 checksums for the ZIP, installer and payload.
7. Create the approved tag and manually upload the installer, ZIP and `SHA256SUMS` to GitHub Releases.

The ZIP updates existing launchers. The installer is the first-install download.
A local build or pushed tag alone does not publish any release assets.

## Installer base payload

Use `scripts/Prepare-EmptyDistroPayload.ps1` and `scripts/Build-Installer.ps1` from
a reviewed `Dwemer-Dynamics/DwemerDistro-Installer` commit. Record that commit and
the clean Core commit with the candidate; there are no workflow pins or CI secrets.

Prepare a sanitized, server-free `DwemerAI4Skyrim3.tar` and its manifest from a
disposable baseline, with the matching `Misc`, `Tools` and convenience resources.
Never export a user's live distro. Verify the tar's SHA256 against its manifest
before packaging. The builder validates fresh identities, absent game servers and
account data, portable pgvector, PHP security and the Assistant runtime proof.

The distro tar must stay at or below 4,200,000,000 bytes. Verify that the finished
installer is below 2 GiB and consists of one executable before publication. Keep
its exact source commits, input hashes and validation evidence with the release.