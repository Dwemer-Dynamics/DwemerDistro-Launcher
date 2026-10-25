# Community mod releases

Open **Mods → Custom mods → Browse community mods**. Browsing uses public GitHub APIs
and does not start WSL. Search author/game names or paste a public repository URL.
Discovery uses topic `kagrenac-mod`; listing is not code review or gameplay endorsement.
The browser shows up to 30 repositories per query; narrow the search for more results.
No install is offered until a stable release with a valid descriptor is checked.

The author publishes `kagrenac-release.json`, a server ZIP and a native-client ZIP.
Package selection/exclusions happen before publication. The launcher never clones the
whole monorepo into the game or server. Artwork comes from the pinned source commit,
with bounded PNG/JPEG downloads and a placeholder on failure.

1. Review the author, version, declared game build and installation paths.
2. Confirm the game directory (Steam detection is offered when unambiguous).
3. Close the game. Install its declared loader first if it is missing.
4. Confirm the plan, then install matching client and server packages.
5. Follow any remaining native mod-menu/load-order instruction and configure providers
   through the server dashboard. Normal game loading owns automatic connection.

The initial `game-directory` profile supports relative file placement and a private
Kagrenac JSON config, not arbitrary post-install scripts or universal engine adapters.
An optional executable hash enforces an exact game build; other compatibility text is
author-reported. No games are launched, provider keys reused or paid provider tests run.
Native UI, voice and action acceptance remain separate from installation.

Core owns server identity, protected named roots, database, backups and package receipts.
The launcher owns the Windows receipt under `%LOCALAPPDATA%/DwemerDistro/CommunityMods`.
These are separate operations, not a cross-filesystem atomic transaction. The installed
list distinguishes server state from the current Windows user's client receipt.

**Update** on a release installation opens its package review. Private client config is
kept byte-for-byte when pairing still agrees; changed pairing refuses instead of silently
overwriting it. Updated archives remove obsolete owned files and retain unknown files.
An interrupted installation retries the same descriptor, including when a newer release
appears. A failed server migration does not imply database rollback; Core retains backup.

**Remove installation** removes only hash-verified owned client files and unregisters
the server. Private config, memories, server files/database and unknown files remain.
Reinstalling the same repository restores the existing server and private pairing.
Legacy **Add custom mod**, branch switching, backups and server-only unregister keep their
existing behavior. New manifest version 2 requires the matching Core package-support PR;
an older Core returns an error without installing a partial client.

Validation uses the existing launcher regression executable. The opt-in
`--community-fixture <release-folder> <game-folder> <private-state-folder> <candidate-core>`
path supplies GitHub API/asset fixtures while exercising actual Windows file placement,
ACL pairing and candidate WSL install/update. `--community-remove` uses the same arguments.
These are developer checks, not live GitHub publication, loader or gameplay evidence.
