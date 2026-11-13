# Debian Bookworm to Trixie maintenance

The **Upgrade DwemerDistro** button beside **Start** opens the Debian 13 upgrade confirmation. Settings > Update Distro handles routine updates.
A fresh OS upgrade requires explicit confirmation. After confirmation, the launcher manages the backup, upgrade and recovery.
Core owns every change inside the distro through
`/usr/local/bin/ddistro_trixie_maintenance` and its host protocol
(Core's `TRIXIE.md`). The launcher owns the Windows side: the offline recovery
copy, its isolated proof boot, disk-space and identity checks, the automatic
restore before the gameplay-write boundary, and a durable journal. Rollout is
**off**. A lab enables it only by hand-editing
`%LOCALAPPDATA%\DwemerDistro\update-preferences.json` to contain
`"TrixieMaintenanceLab": true`. There is no UI for it.

Core fences itself: while `/etc/dwemer/trixie-upgrade.fence` exists, `start_env`
starts no service and `update.sh` and PG17 maintenance exit 75, whatever any
launcher (including an older one without this step) does.

## Launcher sequence

Launcher reopen and manual Update Distro both recover unfinished maintenance. A new OS upgrade requires confirmation from **Upgrade DwemerDistro**.

1. Routine updates retain the existing wait for supported games. A fresh Debian upgrade requires a separate click with games and the server closed. Closing a game does not start a previously refused upgrade.
2. **Recovery first.** An unfinished journal for this registration is resolved before anything
   else writes, including the generic updater (`git reset`, `update.sh`).
3. Core provisioning, shared components and the PostgreSQL 17 step, as before, except that the checkout
   is guarded instead of reset (see Local changes).
4. The Trixie step, only once `ddistro_pg17_maintenance status` reports `current`. The check runs as root with
   `--exec`, so it never starts PostgreSQL or `start_env`; a Core whose `status` needs a running PG17 fails on a cold
   distro, and the step then waits (the reason is in the operations log). Core owns proving a stopped PG17.
5. Release marker. It is skipped while the journal blocks writes.

Interrupted maintenance resumes even when the launcher version is already recorded. Once `Completed`, later launches make no WSL call.

Every Core call is `wsl -d DwemerAI4Skyrim3 -u root --exec flock -n -E 75 --close
/home/dwemer/.dwemerdistro-launcher-update.lock ddistro_trixie_maintenance <verb> ... --json`.
The proof reads (`cat` of Core's journal, `test -f` of the fence, `pg_controldata`) and every call
into the recovery copy are `wsl -d <name> -u root --exec ...`, which starts the distro without
`start_env`. Exit 75 (either lock) means "busy, nothing changed".

### Fresh start

| Step | Call | Proceeds when |
|---|---|---|
| tool | `sha256sum ddistro_trixie_maintenance` | 64 hex; otherwise no tool, not needed |
| provenance | `git hash-object` of the tool, matched against every blob of `bin/ddistro_trixie_maintenance` in the checkout's fetched `origin/main` | a match; otherwise `NotStarted`, the tool is not run |
| status | `status --json` | `state: needed`, `release: "12"`, `codename` `bookworm` or empty, `fence` not true |
| games | — | no supported game, server stopped |
| preflight | `preflight --json` | exit 0, `result: ready`, 32-hex `plan_id`, `used_bytes` > 0, `upgrade_bytes` > 0 |
| space | — | see below; the recovery folder must be on the disk's volume |
| games | — | again, right before cutover |
| cutover | exclusive distro slot | registration unchanged, no game or server |
| prepare | `upgrade --prepare --plan-id ID --json` | `result: fenced`, 64-hex `capture_token`; the token is DPAPI-protected into the journal |
| fence proof | `status`, `cat state.json`, `test -f fence`, `pg_controldata` on the original | state `fenced`, journal token equal, phase `fenced`, plan equal, fence present, PG17 control data equal to Core's seal and `shut down`; the seal is recorded |
| export | `wsl --terminate`, `wsl --export` | stopped before and after, registration (GUID, BasePath, VhdFileName, disk file id) unchanged, whole tar, SHA-256 |
| copy proof | `wsl --import <DwemerAI4Skyrim3-TrixieRecovery-…> <beside the distro> <tar> --version 2`, then the same proof inside the copy | new GUID, its own disk on the distro's volume, the same token, phase, fence and seal; the copy is terminated afterwards |
| upgrade | `upgrade --capture-token T --recovery-sha256 H --recovery-bytes N --json` | `result: upgraded` |
| verify | `wsl --terminate`, `verify --json` | `result: verified`; exit 4 restarts and verifies again, up to 3 times |
| boundary | journal `Releasing`, `GameplayWriteBoundary: true` | written and flushed before release |
| release | `release --json` | `result: current` and `restore.pending` not true; exit 3 runs `verify` once more, still fenced, and releases again |

`release` and `upgrade --cancel` report `restore` (`pending`, `pg17`, `services`, `running`,
`failed`, `problems`): Core starts again PG17 and the writers that ran when it fenced, never on a
`-TrixieRecovery-` registration. `status` reports `restore_pending: true` while that is unfinished.
A reply without `restore` (a Core from before it) is accepted as before.

`instance_id` and `codename` only corroborate: both present and different refuses; either
missing proves nothing. The registration (GUID, BasePath, VhdFileName and the disk's
volume serial and file index) is checked again before each change. The disk is always
`BasePath\VhdFileName` from the registration; `ext4.vhdx` is never assumed.

### What a failure does

| Where | Action |
|---|---|
| Before `prepare` (status, preflight, space, games, lock) | Nothing changed. Core refusals record `Cancelled`, retried after 24 h or a new tool. |
| `prepare` deferred or busy | Nothing changed; `Cancelled`. A deferral after Core fenced and lifted its own fence with `restore_pending` is handled as a cancel whose services are pending. |
| Cancel or release lifted the fence but `restore.pending` (or `restore_pending`) | Not settled: the journal stays `Cancelling` (with `FenceLifted`), `Restoring` step 4 (with `FenceLifted`) or `Releasing`, writes stay blocked, and the next launch runs only `upgrade --cancel` or `release` again: no preflight, export, import or upgrade. `failed` and `problems` are shown. |
| Fence lifted (`FenceLifted`, or `status` without the fence) and a later cancel refused | `RecoveryRequired`: writers may have run, so no disk is swapped. |
| `prepare` failed; fence proof, export, copy import or copy proof failed; export raced | **Cancel**: `upgrade --cancel --json`, then `status` must show `needed`/`deferred` with no fence. An unverified partial export (launcher-named, in the backup folder) is deleted; nothing else is. |
| `upgrade` exit 3 with Core back at `fenced` (plan changed, fence check) | Cancel. |
| `upgrade` failed and `status` is `interrupted`/`fenced` | Rerun with the same token, digest and size, up to 3 attempts in total across launches; then cancel if Core allows it, otherwise **restore**. |
| `verify` exit 3, or still failing after 3 restarts | **Restore** (before the boundary nothing has written). |
| `release` refused twice, or anything after the boundary | `RecoveryRequired`: no automatic restore. |
| An unknown or unparseable Core state, a registration or disk that is not the recorded one | `RecoveryRequired`: nothing automatic. |
| Core reports a fence or upgrade with no launcher journal | `RecoveryRequired`; never cancelled or resumed by guesswork. |

### Restore (before the boundary, no click)

Only when the copy booted and proved itself (`CopyVerified`), the boundary is not set,
and both registrations still match the journal:

1. Terminate both distros; re-prove the copy (it may have been started by hand).
2. Swap disks with three same-volume, write-through, no-replace renames, journaled before each:
   failed disk → `<VhdFileName>.trixie-failed-<id>.vhdx` beside it; recovery disk → the original's
   `BasePath\VhdFileName`; failed disk → the copy's disk path. Each file must open exclusively first.
   `wsl -t` returns before WSL detaches a disk, so the restore polls (1 s, up to 90 s in total,
   cancelled with the update) until every disk still to move opens exclusively. It never runs
   `wsl --shutdown` or closes another process's handle; a distro that starts during the wait stops
   the restore. Before each rename the file ids, both registrations and the journal (`Restoring`,
   copy proven, boundary not set) are checked again; a change is `RecoveryRequired`, nothing more moved.
3. A failed rename, or a disk still open after the wait, is rolled back; the step stays blocked and
   retries next launch (up to 3 times), saying whether any disk had moved. Stopping the update
   during the wait rolls back the same way without counting as an attempt.
   A crash between renames is finished from wherever the files are, identified by file id.
   Files that are not the recorded ones stop everything (`RecoveryRequired`).
4. Boot the original registration, now on the recovery disk: same GUID, folder and disk name,
   the recovery disk's file id, `status` fenced, same token and seal. Then `upgrade --cancel`,
   and `status` must show `needed`/`deferred` with no fence. If Core's restore is pending, the
   journal records `FenceLifted` and stays `Restoring` at step 4; the next launch checks the same
   registration and disk file id and Core's own journal (this token, phase `cancelled`, or
   `fenced` with the fence file gone) and runs only `upgrade --cancel` again. No disk moves again.
5. `Restored`: writers are allowed again. The failed disk stays registered as the copy's WSL distro;
   the tar stays. The same tool is not tried again.

Nothing is unregistered, imported over the original or deleted. The launcher does not
start services itself; Core's `release` and `upgrade --cancel` start again what ran before the
fence, and the launcher reports the step done only once Core says so.

## Local changes

During the migration (see Migration window), Update Distro (manual, startup sync, Quickstart and the mod-update batch alike) does not run
`git reset --hard origin/main` in `/home/dwemer/dwemerdistro`. It fetches `main` explicitly into
`refs/remotes/origin/main` and runs `CoreLocalChangesGuard` first. Stock bytes are only blobs that some commit
reachable from that fetched main had at the same path (`git log --full-history -m --raw`); nothing else is
assumed to be stock.

| Found | Action |
|---|---|
| Nothing local, or files that are an earlier shipped version (older installer, earlier update) | Reset as before. |
| An edit to `*.md`, `LICENSE*` or `tests/` that main did not change since the local base | Kept in the worktree as an unstaged change; saved as `refs/dwemerdistro/kept/<time>-<id>`. |
| Local commits | Saved in that ref (a parent of it); the edits they make follow the same rules. |
| Untracked files outside `bin/` and `etc/` that main does not install | Left alone. |
| A local edit that main also changed; any tracked edit or deletion outside `*.md`, `LICENSE*` and `tests/` (so `update.sh`, `bin/`, `etc/`, `system-release.json`); any extra file under `bin/` or `etc/`, which update.sh would install as root | **Stop**, exit 65. |
| An untracked file, link or folder where main installs something else; an unfinished merge or rebase; assume-unchanged or skip-worktree entries; history that shares no commit with main | **Stop**, exit 65. |
| An installed file `update.sh` overwrites (`/usr/local/bin/<bin file>`, `/etc/<etc file>` except `php.ini`, `dwemerdistro_services.conf`, `dwemerdistro-lmstudio.json` and `cuda_config`, which update.sh already keeps, plus `/etc/dwemer/shared-key-runtime.json` and `/usr/local/lib/dwemerdistro/*keys*`) that is a link, not a plain file, or no shipped version of its source | **Stop**, exit 65. Root-only files are read with `sudo cat`. |

A stop happens before the first write to any local file: the only changes by then are the fetch, Git objects and the
existing `.gitconfig` and `origin` repairs. The console names up to three paths; the operations log lists each with its
reason. Without a `.git` (first real installer), every installer file is compared the same way; an edited passive file is
kept only when main has only ever had one version of it, otherwise it stops. The release marker and the Trixie step don't
run after a stop, so the update is retried on the next launch. Rollout off, and an ordinary update, keep the plain reset. A stop is a safe
deferral: the startup sync prints it in the console and raises no dialog.

### Migration window

The guard and Core's preserve flag apply only while the update belongs to the migration
(`TrixieUpdateMigration.KeepsLocalChangesAsync`, decided after crash recovery and before the core command):

| Rollout | Journal for this registration | OS read (`[ -x tool ] && VERSION_CODENAME`, root `--exec`, read-only) | Update |
|---|---|---|---|
| off | any | not read | ordinary, exactly as before |
| on | `Completed` | not read | ordinary (just-upgraded distro, and every later reopen or Update Distro) |
| on | none, `Cancelled`, `Restored`, `Superseded`, another registration | `bookworm` | migration |
| on | as above | empty (missing or old Core without the tool; this update installs the Core that reads the flag) | migration |
| on | as above | the read failed | migration (an unknown state never resets local files) |
| on | as above | anything else (`trixie`) | ordinary |

A migration update guards the checkout (any caller). Manual Update Distro and the reopen sync also pass Core's
`DDISTRO_PRESERVE_LOCAL_CHANGES=1`:

- to `update.sh` as `echo 'dwemer' | sudo -S -p '' env DDISTRO_PRESERVE_LOCAL_CHANGES=1 ./update.sh`. sudo resets the
  environment, so an exported or prefixed variable would never reach it; `env` sets it inside sudo, and update.sh's
  `ddistro_api maintenance` re-exec keeps `os.environ`. update.sh then keeps a valid UTF-8 `/etc/default/locale`, a
  site-specific valid Dashboard keys sudoers file, and the modes of `/usr/local/bin` files it did not copy.
- to `update_gws` as a plain prefix: the update runs as dwemer through `runuser`, with no sudo before it, and its
  `flock ... env ... bash "$0"` re-exec keeps it. Components are then only fast-forwarded when nothing is lost.

Starting an OS upgrade also requires the user to confirm through **Upgrade DwemerDistro**. Routine updates do not grant that permission. Quickstart (a new install) and the mod-update batch keep
Core's plain update.sh and update_gws and only resume or recover an upgrade already under way; the startup step that
runs when this launcher's release is already recorded runs no update.sh or update_gws at all.

Lifespan: while the migration is pending (including after a restore to Bookworm, until Core ships a new tool),
components with local edits or installer changes stay as installed, and every reopen and Update Distro passes the flag
again, so a second run never undoes what the first kept. Once the journal is `Completed` for this registration, or the
distro reads Trixie, updates are ordinary again, so component installers and dependencies update; Core's ordinary
update may then reset local component edits and the locale, as it always did outside the migration. Core has not
defined a deferral exit code for update_gws yet, so a component refusal still reads as a shared-components failure.

## Startup notice

Only a step that leaves writes blocked raises one, and never a deferral (the step retries it; the console and Start
Server's refusal explain it). The notice reads the journal after the step:

| Journal | Says |
|---|---|
| Unreadable, or not read | It can't tell how far an upgrade got; don't import or restore a backup; Update Distro, then logs and help. |
| `GameplayWriteBoundary` set, or `Releasing`/`Completed` | The upgraded distro may already hold new game progress; don't restore the Bookworm copy. |
| Before the boundary, `CopyVerified` with a digest | Names the checked copy (made inside Core's fence); asks for help before restoring it. |
| Before the boundary, no checked copy | No checked copy exists; don't import an older backup. |

Core's own reason is shown as the details in every case.

## Windows journal

`%LOCALAPPDATA%\DwemerDistro\system-upgrade-journal.json`, schema 2, written through a temp
file with write-through and flush and moved over the old one with `MOVEFILE_WRITE_THROUGH`
before every step. A `.lock` beside it is held exclusively for a whole run, so a second
launcher changes nothing. An unreadable file or another schema blocks writes and is never
overwritten. A set `GameplayWriteBoundary` can't be cleared for that registration.

Phases: `Preparing`, `Fenced`, `Exporting`, `Exported`, `ProvingCopy`, `CopyProven`,
`Upgrading`, `Upgraded`, `Releasing`, `Cancelling`, `Restoring`, `RecoveryRequired`
(all blocking), and `Completed`, `Cancelled`, `Restored`, `Superseded` (not blocking).

| Journal on next launch | Action |
|---|---|
| `Preparing` to `ProvingCopy`, `Cancelling` | Cancel (Core repeats a cancel once `cancelled`, to finish its restore). |
| `CopyProven`, `Upgrading` | `status`: `fenced`/`interrupted` resumes with the same token; `upgraded` verifies; `verified` crosses the boundary and releases; `current` releases again (completes, or finishes Core's restore); no answer stays blocked; anything else is `RecoveryRequired`. |
| `Upgraded` | Verify (`current` releases again). |
| `Releasing` | Release again (Core's `release` is crash-idempotent and repeatable once `current`). |
| `Restoring` | Continue the swap, or finish the restored boot and cancel; with `FenceLifted`, cancel again only. |
| `RecoveryRequired` | Read-only `status`; `current` without a fence completes it, or releases again when `restore_pending`. |
| Another registration GUID | `Superseded`. |
| Unreadable | Blocked, with no WSL call. |

Blocked means no server start, no mod, component, custom-mod, database, Compact or
DNS repair work, no first-time setup, API keys, CUDA, Distro Doctor or Clean Logs window, and
no automatic assistant connection. Update Distro (which resumes), Export, Import, the
terminal and log viewers stay available.

## Space

Per Windows volume; needs on a shared volume add up.

| Need | Volume | Bytes required |
|---|---|---|
| Recovery tar | backup folder | used + 10% + 1 GiB |
| Upgrade growth | volume holding `BasePath\VhdFileName` | upgrade + 25% + 1 GiB |
| Isolated recovery copy | the same volume (required) | used + 20% + 2 GiB |

The restore itself only renames.

## Host proof protocol (isolated lab only)

Run only on a lab machine or user profile with no live data. Record every result.

1. Import a disposable Bookworm copy as `DwemerAI4Skyrim3` with PostgreSQL 17 `current`, set
   the lab opt-in, and install a Core that contains the tool and `release`.
2. **Happy path:** open the launcher and verify that no OS upgrade starts. Choose **Upgrade DwemerDistro** beside **Start** and confirm. The journal goes `Preparing` … `CopyProven`, `Upgrading`,
   `Upgraded`, `Releasing`, `Completed`. `Get-FileHash` of the tar matches the journal and
   Core's `state.json` `recovery.sha256`. The copy distro exists and booted fenced. Reopen:
   no Trixie WSL calls.
3. **Export race:** start `wsl -d DwemerAI4Skyrim3` during the export. Expect `Cancelled`, the
   partial tar deleted, Core `needed` without a fence.
4. **Kill during export or copy import:** reopen; expect cancel.
5. **Kill during upgrade:** reopen while Core still runs (exit 75: blocked). After Core ends,
   reopen: same token resumes, verifies and releases.
6. **Forced failure past dpkg** (for example break a package after `upgrade` started): expect
   retries, then `Restored`: the original GUID boots the Bookworm copy, Core `needed`, the copy
   distro now holds the failed disk, the tar is kept.
7. **Kill during the disk swap** at each step; reopen and confirm it finishes. Lock one disk
   (open it in another process) for under 90 s and confirm the restore finishes in the same run;
   keep it locked and confirm nothing moves, writes stay blocked and the next launch finishes it.
8. **Release refused:** change PG17 after `verify`; expect one more `verify`, then release.
9. **Services not restarted** (the ASUS case): occupy the application socket so PG17 can't start,
   once each after `release`, after the restored disk's cancel and after a pre-apt cancel. Expect
   writes blocked, `failed`/`problems` shown, Core `restore.pending` in `state.json`, no disk moved;
   free the socket and reopen: only `release` or `upgrade --cancel` runs, PG17 and the writers
   that ran before are up, then `Completed`, `Restored` or `Cancelled`.
10. **Rollout off:** delete the opt-in. A fresh Bookworm distro behaves exactly as before:
   no stage, no WSL call, no journal.

## Open gaps

- Not run against WSL or a real Bookworm image; only the scripted regression checks ran.
- The capture token is passed on the `wsl.exe` command line, visible to the same Windows user.
- Copies left after a cancel are kept and named; the launcher never removes them.
- Older launchers ignore this journal; only Core's fence protects the distro from them.
- The guard mirrors update.sh's install targets; Core should own that list (a manifest), or protect them itself.
- Outside a migration run that passes the flag (Quickstart, the mod-update batch, and every update after completion),
  update.sh rewrites `/etc/sudoers.d/dwemerdistro-dashboard-keys` and `/etc/default/locale` (`update-locale`) as before.
  Even with the flag, its helpers (`ensure_php_security`,
  `initialize_fresh_distro`, `install_assistant_runtime`, `install_shared_keys`) write system files with no shipped
  baseline. `update_gws` runs `git reset --hard HEAD` in minime-t5, MeloTTS, remote-faster-whisper, piper,
  xtts-api-server, parakeet-api-server, chatterbox, audio.cpp, pocket-tts and omnivoice-tts, discarding tracked edits there,
  unless it honours `DDISTRO_PRESERVE_LOCAL_CHANGES=1`. Core's current `update_gws` does: it only fast-forwards, skips
  `update_perms` and the servers, and exits 0 for components it left as installed (1 only for a failure, which stops
  the PostgreSQL 17 and Trixie steps). Components left as installed are not retried until the next release.
- While Core's fence stands, `update.sh` exits 75 with `[ERROR] A Debian 13 upgrade is in progress`. The launcher
  reports that line as the failure, not as another update holding the lock (only reached when no blocking journal
  exists, for example after the launcher's local data was removed).
- Two edits to the same passive file are not merged; the update stops instead.
- The installed-file check has not run against a real image: a stock file that some image step rewrote would stop the update.
