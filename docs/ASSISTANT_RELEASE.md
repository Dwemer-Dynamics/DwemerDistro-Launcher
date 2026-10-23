# Assistant release preparation

The Assistant release spans Core, Launcher and Installer. A Launcher-only release
does not provision existing distros or refresh the installer image.

## Candidate contract

- Core provides Assistant API version 1. Launcher refuses missing or incompatible
  APIs with Update Distro guidance before opening a session.
- Core upgrades install the pinned Codex runtime without signing in. Returning
  users keep their account files; fresh users complete their own sign-in.
- Maintenance drains the API only when no request or job is running. Busy work
  defers the update. Auto-connect cannot restart the API during replacement or
  isolation probing. Failed checks prevent successful update reporting.
- Legacy `ddistro agent serve` clients use the same supervised API relay.
- Game clients remain chat-only. They cannot use Launcher maintenance permissions,
  account controls, approvals, saved memory or another client's conversation.
- A fresh chat does not require an installed game server. File selection still
  requires the corresponding installed server and existing ownership checks.
- Isolation attestations are bound to the installed filesystem and kernel.
  Compatible Core advertises a guarded automatic recheck on first connection
  after import or a stale binding. A failed check leaves Assistant off.

## Build and validate

1. Build a clean Core commit with Installer's `Prepare-EmptyDistroPayload.ps1`.
   Use a disposable imported baseline, never an export of a user's live distro.
2. Verify fresh identities, absent game data and empty Assistant account, memory,
   task and API state. The manifest must record successful isolation and hashes
   for the pinned runtime and every Assistant helper.
3. Publish Launcher and Updater locally as self-contained win-x64 single-file
   applications. Package the exact published output as `DwemerDistro-win-x64.zip`.
4. Pass that ZIP and the verified tar to Installer's `Build-Installer.ps1`.
   The builder independently checks runtime hashes and rejects inherited state.
5. Test legacy Core upgrades, custom configuration and database preservation,
   idle/busy maintenance, failed checks and retry, helper-generation rollback,
   fresh chat creation and signed-out connection. Test the packaged executables.
6. Keep Windows installer execution on an existing owned distro separate from a
   successful fresh import. A clean Windows/WSL install and first personal sign-in
   require their own evidence; a local build is not that evidence.

## Publication gate

Manual workflow runs build validation artifacts and never publish releases.
`payload_url`, `payload_sha256` and `distro_tar_sha256` allow a reviewed payload to
be validated without changing the stable download. The workflow requires its
manifest to match `EXPECTED_CORE_COMMIT` and the Assistant contract.

Before a production tag, approve and merge the dependency PRs, choose fresh
Launcher and Core release versions, promote the verified payload's immutable URL
and both hashes, and revalidate that exact source. The retained older payload
defaults intentionally fail the new contract until that promotion is complete.
Do not tag or publish the existing-version local candidate.
