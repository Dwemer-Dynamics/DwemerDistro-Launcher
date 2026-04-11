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
4. upload the zip to GitHub Releases

That release zip becomes the update payload the installed launcher reads.
