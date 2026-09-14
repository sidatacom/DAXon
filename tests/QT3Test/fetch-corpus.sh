#!/bin/sh
# Downloads the W3C test corpora this runner needs, at the revisions pinned in corpus.pin.
#
# The corpora are not part of this repository: they are large, they belong to the W3C, and
# vendoring them would pin thousands of third-party files into our history.
#
# The failure baseline in BASELINE-FAILS.txt was measured against exactly the pinned
# revisions, so a run over anything else may differ for reasons unrelated to the engine.
#
#   ./fetch-corpus.sh            pinned revisions (what the baseline describes)
#   ./fetch-corpus.sh --latest   current upstream HEAD instead
#   ./fetch-corpus.sh --force    re-download even if already at the right revision
set -eu

root=$(cd "$(dirname "$0")" && pwd)
latest=0
force=0
for arg in "$@"; do
    case "$arg" in
        --latest) latest=1 ;;
        --force)  force=1 ;;
        *) echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

[ -f "$root/corpus.pin" ] || { echo "corpus.pin not found next to this script" >&2; exit 2; }

total=0
for repo in qt3tests xslt30-test; do
    if [ "$latest" -eq 1 ]; then
        revision=$(curl -sS -H 'User-Agent: DAXon-QT3Test' \
            "https://api.github.com/repos/w3c/$repo/commits?per_page=1" \
            | sed -n 's/.*"sha" *: *"\([0-9a-f]\{40\}\)".*/\1/p' | head -1)
    else
        revision=$(sed -n "s/^$repo=//p" "$root/corpus.pin" | tr -d '[:space:]')
    fi
    [ -n "$revision" ] || { echo "no revision for '$repo'" >&2; exit 2; }

    target="$root/$repo"
    stamp="$target/.corpus-revision"
    if [ "$force" -eq 0 ] && [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$revision" ]; then
        echo "$repo already at $(echo "$revision" | cut -c1-12) - skipping"
        continue
    fi

    archive="${TMPDIR:-/tmp}/$repo-$revision.tar.gz"
    if [ ! -f "$archive" ]; then
        echo "downloading $repo @ $(echo "$revision" | cut -c1-12) ..."
        # codeload is where github.com/<owner>/<repo>/archive/... redirects to. Ask it directly:
        # the redirecting front end returns 504 on the larger of these two archives often enough
        # to matter, while codeload serves it. -f so an HTTP error is a failure rather than a
        # saved error page.
        curl -fsSL --retry 3 --retry-delay 5 -o "$archive" \
            "https://codeload.github.com/w3c/$repo/tar.gz/$revision" \
            || { rm -f "$archive"; echo "download of $repo failed" >&2; exit 1; }
    fi

    echo "extracting $repo ..."
    rm -rf "$target"
    mkdir -p "$target"
    # --strip-components drops the "<repo>-<sha>/" wrapper GitHub puts in source tarballs.
    tar -xzf "$archive" --strip-components=1 -C "$target"
    printf '%s' "$revision" > "$stamp"
    rm -f "$archive"

    echo "$repo ready ($(du -sm "$target" | cut -f1) MB)"
done

for repo in qt3tests xslt30-test; do
    [ -d "$root/$repo" ] && total=$((total + $(du -sm "$root/$repo" | cut -f1)))
done

echo ""
echo "corpora ready under $root (${total} MB total)"
echo "run:  dotnet build tests/QT3Test -c Release"
echo "      tests/QT3Test/bin/Release/net472/QT3Test.exe qt3tests xslt30-test"
