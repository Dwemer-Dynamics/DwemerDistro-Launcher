namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// With the Debian Trixie rollout on, this replaces the Core checkout's plain <c>git reset --hard origin/main</c>. Before
/// anything changes it compares the checkout, its untracked files and the files <c>update.sh</c> installs against every
/// blob the official main branch ever had: the only bytes it treats as stock. A local change the update would overwrite,
/// any local change to something <c>update.sh</c> installs or runs as root, and an installed file that isn't a shipped
/// version all stop the update with exit <see cref="DeferredExitCode"/>, and no local file is changed. A change to
/// documentation or tests that the update leaves alone is kept in the worktree and saved under
/// <c>refs/dwemerdistro/kept/</c>, as are local commits.
/// </summary>
internal static class CoreLocalChangesGuard
{
    internal const int DeferredExitCode = 65;
    internal const string KeptRefPrefix = "refs/dwemerdistro/kept/";

    /// <summary>
    /// Fetches main explicitly, so a custom refspec can't stand in for it, then runs the guard and the checkout sync in
    /// the checkout. <paramref name="deployRoot"/> prefixes the installed paths; only the regression fixture sets it.
    /// </summary>
    internal static string BuildFetchAndSyncCommand(string deployRoot = "") =>
        "git -c credential.helper= fetch origin +refs/heads/main:refs/remotes/origin/main && (\n" +
        Script.Replace("@DEPLOY_ROOT@", deployRoot.Replace("'", "", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("@DEFERRED_EXIT@", DeferredExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) + ")";

    // Mirrors update.sh: bin/* to /usr/local/bin, every etc file but the four it already keeps to /etc, and its explicit
    // installs to /etc/dwemer and /usr/local/lib/dwemerdistro. Generated files (the sudoers rule) are Core's to protect.
    private const string Script = """
        set -o pipefail
        export GIT_NO_REPLACE_OBJECTS=1 GIT_LITERAL_PATHSPECS=1 LC_ALL=C
        deploy='@DEPLOY_ROOT@'
        work="$(mktemp -d)" || exit 1
        trap 'rm -rf "$work"' EXIT
        : > "$work/conflicts"; : > "$work/keep"
        conflict() { printf '%s: %s\n' "$1" "$2" >> "$work/conflicts"; }
        object() { git rev-parse -q --verify "$1:$2" 2>/dev/null; }
        passive() { case "$1" in bin/*|etc/*) return 1;; tests/*|LICENSES/*|LICENSE|*.md) return 0;; esac; return 1; }
        O="$(git rev-parse -q --verify 'refs/remotes/origin/main^{commit}')" || { echo '[ERROR] DwemerDistro-Core main was not fetched.'; exit 1; }
        git log --root --full-history -m --no-renames --no-abbrev --raw --format= "$O" \
            | awk -F '\t' 'NF > 1 { split($1, f, " "); if (f[3] !~ /^0+$/) print $2 " " f[3]; if (f[4] !~ /^0+$/) print $2 " " f[4] }' \
            | sort -u > "$work/known" || exit 1
        known() { grep -qxF -- "$1 $2" "$work/known"; }
        # The official installer image ships system-release.json, a data marker nothing runs, with CRLF line endings
        # where main has LF. Only that file, only without a checkout, and only when every line ends in exactly one CRLF
        # and the LF form is a shipped version is it stock; any other byte difference is still a local change.
        crlf_marker() {
            cr="$(tr -cd '\r' < "$1" | wc -c)" && lf="$(tr -cd '\n' < "$1" | wc -c)" || return 1
            [ "$cr" -gt 0 ] && [ "$cr" -eq "$lf" ] && [ "$(sed 's/\r$//' -- "$1" | tr -cd '\r' | wc -c)" -eq 0 ] || return 1
            known "$1" "$(sed 's/\r$//' -- "$1" | git hash-object --no-filters --stdin)"
        }
        one_version() { awk -v p="$1" '{ if (substr($0, 1, length($0) - length($NF) - 1) == p) n++ } END { exit n != 1 }' "$work/known"; }
        head=; commits=; mode=installer
        if head="$(git rev-parse -q --verify 'HEAD^{commit}')"; then
            mode=repo
            [ -z "$(git ls-files -u)" ] || conflict '(index)' 'an unfinished merge'
            for marker in MERGE_HEAD CHERRY_PICK_HEAD REVERT_HEAD rebase-merge rebase-apply; do
                [ ! -e "$(git rev-parse --git-path "$marker")" ] || conflict '(repository)' "an unfinished $marker"
            done
            ! git ls-files -v | grep -q '^[a-zS] ' || conflict '(index)' 'files marked assume-unchanged or skip-worktree'
            # The index's entries without its stat cache, so every tracked file is hashed: an edit of the same size in
            # the same second as the checkout must not read as clean.
            if [ -f "$(git rev-parse --git-path index)" ]; then git ls-files -s -z | GIT_INDEX_FILE="$work/index" git update-index -z --index-info
            else GIT_INDEX_FILE="$work/index" git ls-tree -r -z "$head" | GIT_INDEX_FILE="$work/index" git update-index -z --index-info; fi || exit 1
            GIT_INDEX_FILE="$work/index" git add -u -- . && cur="$(GIT_INDEX_FILE="$work/index" git write-tree)" || exit 1
            git merge-base --is-ancestor "$head" "$O" || commits=1
            if base="$(git merge-base "$head" "$O")"; then
                git diff --name-only --no-renames -z "$base" "$cur" | while IFS= read -r -d '' p; do
                    c="$(object "$cur" "$p")"; o="$(object "$O" "$p")"
                    [ "$c" != "$o" ] || continue
                    if [ "$(object "$base" "$p")" != "$o" ]; then conflict "$p" 'changed here and in the update'
                    elif passive "$p"; then printf '%s\n' "$p" >> "$work/keep"
                    else conflict "$p" 'changed here; update.sh installs or runs it as root'; fi
                done
            else
                conflict '(history)' 'local history shares no commit with DwemerDistro-Core'
            fi
        fi
        git ls-files -o -z | while IFS= read -r -d '' u; do
            type="$(git cat-file -t "$O:$u" 2>/dev/null)"
            if [ "$type" = blob ]; then
                [ ! -L "$u" ] || { conflict "$u" 'a link where the update installs a file'; continue; }
                h="$(git hash-object -- "$u")" || { conflict "$u" 'unreadable'; continue; }
                [ "$h" != "$(object "$O" "$u")" ] || continue
                ! known "$u" "$h" || continue
                [ "$mode" != installer ] || [ "$u" != system-release.json ] || ! crlf_marker "$u" || continue
                if [ "$mode" = installer ] && passive "$u" && one_version "$u"; then printf '%s\n' "$u" >> "$work/keep"
                else conflict "$u" 'not a version DwemerDistro-Core shipped; the update would overwrite it'; fi
                continue
            fi
            [ -z "$type" ] || { conflict "$u" 'in the way of a folder the update installs'; continue; }
            d="$u"
            while [ "${d%/*}" != "$d" ]; do
                d="${d%/*}"
                [ "$(git cat-file -t "$O:$d" 2>/dev/null)" != blob ] || { conflict "$u" "inside $d, which the update installs as a file"; break; }
            done
            case "$u" in bin/*|etc/*) conflict "$u" 'an extra file update.sh would install as root';; esac
        done
        git ls-tree -r -z --name-only "$O" -- bin etc | while IFS= read -r -d '' p; do
            name="${p##*/}"
            case "$p" in
                bin/*/*) continue ;;
                bin/*) set -- "/usr/local/bin/$name" ;;
                *) case "$name" in php.ini|dwemerdistro_services.conf|dwemerdistro-lmstudio.json|cuda_config) continue;; esac
                   set -- "/etc/$name" ;;
            esac
            case "$p" in
                bin/ddistro_keys.php|bin/ddistro_native_keys.php|bin/ddistro_shared_keys.php|bin/ddistro_legacy_keys.php|bin/ddistro_legacy_keys.sql)
                    set -- "$@" "/usr/local/lib/dwemerdistro/${name#ddistro_}" ;;
                etc/shared-key-runtime.json) set -- "$@" /etc/dwemer/shared-key-runtime.json ;;
            esac
            for t in "$@"; do
                t="$deploy$t"
                if [ -L "$t" ] || { [ -e "$t" ] && [ ! -f "$t" ]; }; then conflict "$t" 'not a plain file'; continue; fi
                [ -e "$t" ] || continue
                if [ -r "$t" ]; then h="$(git hash-object --no-filters -- "$t")"
                else h="$(printf 'dwemer\n' | sudo -S -p '' cat -- "$t" | git hash-object --no-filters --stdin)"; fi \
                    || { conflict "$t" 'could not be read'; continue; }
                known "$p" "$h" || conflict "$t" "changed after it was installed; it is no version of $p"
            done
        done
        if [ -s "$work/conflicts" ]; then
            while IFS= read -r line; do printf '>> Not overwritten: %s\n' "$line"; done < "$work/conflicts"
            printf '[ERROR] Local changes would be overwritten by the update (%s). No local file was changed; move or undo them, then run Update Distro in Settings > First-Time Setup.\n' \
                "$(awk -F ': ' 'NR <= 3 { printf "%s%s", (NR > 1 ? ", " : ""), $1 } END { if (NR > 3) printf ", and %d more", NR - 3 }' "$work/conflicts")"
            exit @DEFERRED_EXIT@
        fi
        if [ -s "$work/keep" ] || [ -n "$commits" ]; then
            GIT_INDEX_FILE="$work/kept" git read-tree "$O" || exit 1
            while IFS= read -r p; do
                if [ -e "$p" ] || [ -L "$p" ]; then GIT_INDEX_FILE="$work/kept" git add -f -- "$p"
                else GIT_INDEX_FILE="$work/kept" git rm -q --cached --ignore-unmatch -- "$p"; fi || exit 1
            done < "$work/keep"
            tree="$(GIT_INDEX_FILE="$work/kept" git write-tree)" || exit 1
            saved="$(GIT_AUTHOR_NAME='DwemerDistro Launcher' GIT_AUTHOR_EMAIL=launcher@dwemerdistro.invalid \
                GIT_COMMITTER_NAME='DwemerDistro Launcher' GIT_COMMITTER_EMAIL=launcher@dwemerdistro.invalid \
                git commit-tree "$tree" -p "$O" ${head:+-p "$head"} -m 'Local changes kept by the DwemerDistro Launcher update')" || exit 1
            ref="refs/dwemerdistro/kept/$(date -u +%Y%m%d-%H%M%S)-${saved:0:12}"
            git update-ref "$ref" "$saved" || exit 1
            printf '>> Local changes and commits saved as %s\n' "$ref"
        fi
        if [ -s "$work/keep" ]; then
            git read-tree --reset -u "$saved" && git reset -q "$O" || exit 1
            sed 's/^/>> Kept local change: /' "$work/keep"
        else
            git reset --hard "$O" || exit 1
        fi
        """;
}
