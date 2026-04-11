# Launcher Updates

This launcher is configured to use GitHub Releases from:

- `https://github.com/Dwemer-Dynamics/DwemerDistro-Launcher`

The WPF app checks that repo for new Velopack releases and can download/apply launcher-only updates.

## What updates

- `DwemerDistro.exe`
- other launcher package files managed by Velopack

## What does not update

- the installed `DwemerAI4Skyrim3` WSL distro
- the large distro payload
- user data inside the WSL instance

## User flow after distribution

1. Install the packaged launcher once from a Velopack-built release.
2. Launch `DwemerDistro.exe`.
3. The launcher checks GitHub Releases.
4. If a newer launcher exists, the bottom `Launcher` section shows an update is available.
5. Clicking `Update Launcher` downloads the new launcher package and restarts the app to apply it.

## Release flow

Push a tag like:

```powershell
git tag v2.5.2
git push origin v2.5.2
```

The GitHub Actions workflow will:

1. publish the WPF app
2. pack a Velopack release
3. upload release assets to GitHub Releases

Those release assets become the update feed the installed launcher reads.
