#!/usr/bin/env bash
# Cross-publish self-contained single-file Windows builds (no .NET install needed on the target).
# Usage: ./publish-win.sh [win-x64|win-x86|win-arm64|all]      (default: win-x64)
#   all  publishes every runtime above in parallel
set -euo pipefail
cd "$(dirname "$0")"   # always run from the repo root, whatever the caller's cwd

RIDS=(win-x64 win-x86 win-arm64)
APP=src/NetManager.App/NetManager.App.csproj

# Every publish run is a new build: bump the counter that the app shows in its status bar ("build 7").
# (All runtimes published by one `all` run share the number.)
BUILD_NUMBER=$(( $(cat build-number.txt 2>/dev/null || echo 0) + 1 ))
echo "$BUILD_NUMBER" > build-number.txt
echo "== build $BUILD_NUMBER =="

# publish_one <rid> [extra dotnet args…]
publish_one() {
  local rid="$1"; shift
  dotnet publish "$APP" -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -p:BuildNumber="$BUILD_NUMBER" \
    -o "publish/$rid" "$@"
  rm -f "publish/$rid"/*.pdb
  echo "→ publish/$rid/NetManager.exe"
}

target="${1:-win-x64}"

if [[ "$target" != "all" ]]; then
  if [[ " ${RIDS[*]} " != *" $target "* ]]; then
    echo "Unknown runtime '$target'. Use one of: ${RIDS[*]} all" >&2
    exit 2
  fi
  publish_one "$target"
  exit 0
fi

# --- all: one isolated build tree per runtime -------------------------------------------------------------
# Parallel publishes of the same project would race on the shared obj/ folders (restore assets, the libraries
# every runtime builds) — the same kind of clash that deletes outputs when two builds overlap. --artifacts-path
# gives each runtime its own obj/bin, so they cannot touch each other (and the normal obj/ used by the IDE stays untouched).
logdir="$(mktemp -d)"
trap 'rm -rf "$logdir"' EXIT

pids=()
for rid in "${RIDS[@]}"; do
  publish_one "$rid" --artifacts-path "artifacts/$rid" >"$logdir/$rid.log" 2>&1 &
  pids+=($!)
done

failed=0
for i in "${!RIDS[@]}"; do
  rid="${RIDS[$i]}"
  if wait "${pids[$i]}"; then
    echo "✔ $rid  →  publish/$rid/NetManager.exe ($(du -h "publish/$rid/NetManager.exe" | cut -f1))"
  else
    echo "✘ $rid failed:"
    sed 's/^/    /' "$logdir/$rid.log"
    failed=1
  fi
done
exit $failed
