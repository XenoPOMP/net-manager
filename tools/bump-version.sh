#!/usr/bin/env bash
# Sets the app version and restarts the build counter, so the next publish is "build 1" of the new version.
# Usage: tools/bump-version.sh <version>
#   release      tools/bump-version.sh 0.2.0
#   pre-release  tools/bump-version.sh 0.2.0-beta.1      (also -alpha.N, -rc.N …)
# <version> must be SemVer 2.0: MAJOR.MINOR.PATCH[-prerelease]; no leading zeros, no "v" prefix, no +metadata
# (the SDK appends "+<git sha>" itself). Afterwards: dotnet build, dotnet test, ./publish-win.sh all, commit. See CLAUDE.md → Versioning.
set -euo pipefail
cd "$(dirname "$0")/.."

new="${1:-}"
if [[ -z "$new" ]]; then
  echo "Usage: tools/bump-version.sh <version>   (e.g. 0.2.0, 0.2.0-beta.1, 1.0.0-rc.1)" >&2
  exit 2
fi

# semver.org grammar, without build metadata. A numeric pre-release identifier may not have leading zeros ("beta.01" is invalid).
ident='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
semver="^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-${ident}(\.${ident})*)?\$"
if [[ ! "$new" =~ $semver ]]; then
  echo "'$new' is not a valid version. Use MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-prerelease (0.2.0, 0.2.0-beta.1, 1.0.0-rc.2)." >&2
  echo "No leading zeros, no 'v' prefix, no '+metadata', dot-separated pre-release identifiers without empty parts." >&2
  exit 2
fi

props=Directory.Build.props
current="$(sed -n -E 's|.*<Version>([^<]*)</Version>.*|\1|p' "$props" | head -1)"
if [[ -z "$current" ]]; then echo "No <Version> element found in $props." >&2; exit 1; fi
if [[ "$current" == "$new" ]]; then echo "Already at $new — nothing to do." >&2; exit 1; fi

# Pre-releases order by comparing identifiers: numeric ones numerically, others alphabetically ("beta.2" < "beta.10", but "beta2" > "beta10").
if [[ "$new" == *-* ]]; then
  pre="${new#*-}"
  if [[ ! "$pre" =~ \.[0-9]+$ ]]; then
    echo "note: pre-release '$pre' has no numeric last identifier; prefer e.g. '$pre.1' so that successive pre-releases sort correctly." >&2
  fi
fi

sed -E "s|<Version>[^<]*</Version>|<Version>${new}</Version>|" "$props" > "$props.tmp" && mv "$props.tmp" "$props"
previous_build="$(cat build-number.txt 2>/dev/null || echo 0)"
echo 0 > build-number.txt

echo "version      $current → $new"
echo "build number $previous_build → 0   (the next publish is build 1)"
echo "next: dotnet build NetManager.slnx && dotnet test && ./publish-win.sh all   (status bar must read v$new · build 1), then commit Directory.Build.props + build-number.txt"
