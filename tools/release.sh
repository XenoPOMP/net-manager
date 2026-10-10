#!/usr/bin/env bash
# Interactive release: publish all runtimes, pick release notes, (re)tag the current commit, create a GitHub release with the exes.
# Usage: tools/release.sh [--notes claude|placeholder|<file.md>] [--skip-publish] [--yes] [--dry-run]
#   (no flags)       fully interactive (arrow keys / j k / digits, Enter to choose, q to quit)
#   --notes X        skip the notes menu: generate with Claude, use the placeholder text, or read a Markdown file
#   --skip-publish   reuse the exes already in publish/<rid>/ (no new build number, nothing rebuilt)
#   --yes            no confirmation prompts (needs --notes when stdin is not a terminal)
#   --dry-run        do everything except changing git/GitHub: mutating commands are printed instead of run
# Needs: gh (logged in), a git remote, optionally claude. Steps and the reasoning are in CLAUDE.md → Releases.
set -euo pipefail
cd "$(dirname "$0")/.."   # always run from the repo root, whatever the caller's cwd

NOTES_MODE="" SKIP_PUBLISH=0 ASSUME_YES=0 DRY_RUN=0
while (( $# )); do
  case "$1" in
    --notes) NOTES_MODE="${2:?--notes needs claude, placeholder or a file}"; shift 2 ;;
    --skip-publish) SKIP_PUBLISH=1; shift ;;
    --yes|-y) ASSUME_YES=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown argument '$1' (see --help)." >&2; exit 2 ;;
  esac
done

if [[ -t 2 ]]; then BOLD=$'\e[1m' DIM=$'\e[2m' CYAN=$'\e[36m' GREEN=$'\e[32m' YELLOW=$'\e[33m' RED=$'\e[31m' RESET=$'\e[0m'
else BOLD="" DIM="" CYAN="" GREEN="" YELLOW="" RED="" RESET=""; fi
step() { printf '\n%s== %s ==%s\n' "$BOLD$CYAN" "$*" "$RESET" >&2; }
info() { printf '%s\n' "$*" >&2; }
warn() { printf '%s! %s%s\n' "$YELLOW" "$*" "$RESET" >&2; }
die()  { printf '%s✘ %s%s\n' "$RED" "$*" "$RESET" >&2; exit 1; }
# Mutating commands go through run so --dry-run can print them instead.
run() { if (( DRY_RUN )); then printf '%s[dry-run]%s %s\n' "$DIM" "$RESET" "$*" >&2; else "$@"; fi; }

INTERACTIVE=0
[[ -t 0 && -t 2 ]] && INTERACTIVE=1

# menu <title> <option>… — arrow keys / j k / digit to move, Enter to pick, q to quit. Result: MENU_CHOICE (0-based).
menu() {
  local title="$1"; shift
  local opts=("$@") n=$# sel=0 key rest i
  printf '\n%s%s%s\n' "$BOLD" "$title" "$RESET" >&2
  while true; do
    for i in "${!opts[@]}"; do
      if (( i == sel )); then printf '  %s❯ %d. %s%s\e[K\n' "$CYAN" $((i + 1)) "${opts[$i]}" "$RESET" >&2
      else printf '    %d. %s\e[K\n' $((i + 1)) "${opts[$i]}" >&2; fi
    done
    IFS= read -rsn1 key </dev/tty || die "No terminal to read from."
    if [[ "$key" == $'\e' ]]; then
      if read -rsn2 -t 1 rest </dev/tty; then key="$rest"; else key="esc"; fi   # a lone Esc just times out
    fi
    case "$key" in
      '[A'|'OA'|k) sel=$(( (sel + n - 1) % n )) ;;
      '[B'|'OB'|j) sel=$(( (sel + 1) % n )) ;;
      [1-9]) (( key <= n )) && sel=$((key - 1)) ;;
      q) die "Cancelled." ;;
      '') MENU_CHOICE=$sel; return ;;   # Enter
    esac
    printf '\e[%dA' "$n" >&2
  done
}

# confirm <question> — yes by default under --yes.
confirm() {
  (( ASSUME_YES )) && return 0
  (( INTERACTIVE )) || die "Needs a terminal to ask: $1 (pass --yes to accept all)."
  menu "$1" "Yes" "No"
  (( MENU_CHOICE == 0 ))
}

# --- preflight: fail before the (long) build, not after --------------------------------------------------------
step "Checking environment"
command -v gh >/dev/null || die "GitHub CLI (gh) not found. Install it: brew install gh"
gh auth status >/dev/null 2>&1 || die "gh is not logged in. Run: gh auth login"
if git remote | grep -qx origin; then REMOTE=origin; else REMOTE="$(git remote | head -1)"; fi
[[ -n "$REMOTE" ]] || die "This repo has no git remote, so there is nowhere to release to. Add one: git remote add origin <url>"
REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner 2>/dev/null)" || die "gh cannot resolve a GitHub repository from remote '$REMOTE'."
if [[ "$NOTES_MODE" == "claude" ]]; then command -v claude >/dev/null || die "--notes claude needs the claude CLI."; fi
if [[ -n "$NOTES_MODE" && "$NOTES_MODE" != claude && "$NOTES_MODE" != placeholder && ! -f "$NOTES_MODE" ]]; then
  die "--notes: '$NOTES_MODE' is neither claude, placeholder nor an existing file."
fi
(( ASSUME_YES || INTERACTIVE )) || [[ -n "$NOTES_MODE" ]] || die "Not a terminal: pass --notes (and --yes)."

VERSION="$(sed -n -E 's|.*<Version>([^<]*)</Version>.*|\1|p' Directory.Build.props | head -1)"
[[ -n "$VERSION" ]] || die "No <Version> found in Directory.Build.props."
TAG="v$VERSION"
info "repo $REPO · remote $REMOTE · version $VERSION · tag $TAG$( (( DRY_RUN )) && echo ' · DRY RUN' )"

# Anything uncommitted except the build counter means the exes are not built from the commit that gets tagged.
others="$(git status --porcelain | grep -v ' build-number.txt$' || true)"
if [[ -n "$others" ]]; then
  warn "Uncommitted changes (the tag will point at the last commit, not at these):"
  printf '%s\n' "$others" | sed 's/^/    /' >&2
  confirm "Release anyway?" || die "Cancelled."
fi

# --- 1. publish -----------------------------------------------------------------------------------------------
RIDS=(win-x64 win-x86 win-arm64)
asset_name() { case "$1" in win-x64) echo NetManager_x64.exe ;; win-x86) echo NetManager_x86.exe ;; win-arm64) echo NetManager_ARM64.exe ;; esac; }

if (( SKIP_PUBLISH )); then
  step "Skipping publish (using existing publish/<rid>/)"
else
  step "Publishing all runtimes"
  ./publish-win.sh all || die "publish-win.sh failed — nothing was tagged or released."
fi
for rid in "${RIDS[@]}"; do
  [[ -f "publish/$rid/NetManager.exe" ]] || die "publish/$rid/NetManager.exe is missing."
done
BUILD="$(cat build-number.txt 2>/dev/null || echo '?')"
info "version $VERSION · build $BUILD"

# --- 2. release notes -----------------------------------------------------------------------------------------
PLACEHOLDER="Generated release for version $TAG"
NOTES_FILE="publish/RELEASE_NOTES.md"   # kept (publish/ is gitignored) so a failed release can be retried with --notes
mkdir -p publish

# previous release tag = nearest tag reachable from HEAD other than the one being (re)created
PREV="$(git describe --tags --abbrev=0 --exclude "$TAG" HEAD 2>/dev/null || true)"

generate_with_claude() {
  local range ctx prompt out
  if [[ -n "$PREV" ]]; then range="$PREV..HEAD"; else range="HEAD"; fi
  ctx="$(git log --no-merges --pretty='- %s' "$range" | head -n 200)"$'\n\n'"$(git diff --stat "${PREV:-$(git hash-object -t tree /dev/null)}" HEAD | tail -n 40)"
  prompt="Write GitHub release notes in Markdown for NetManager $TAG, a NetworkManager-style network settings app for Windows (IPv4, DNS, routes, adapters, profiles). \
Below are the commit subjects and a diffstat since ${PREV:-the beginning}. Be concise and user-facing; group into short sections (e.g. Features, Fixes) only if there is enough to group; \
skip chores such as build-number or version bumps; do not invent anything the commits do not support. Output only the Markdown, no preamble and no code fence.

$(printf '%s' "$ctx" | head -c 30000)"
  info "Asking Claude (commits since ${PREV:-the beginning})…"
  out="$(claude -p --tools "" --no-session-persistence "$prompt")" || return 1
  [[ -n "${out//[[:space:]]/}" ]] || return 1
  printf '%s\n' "$out" > "$NOTES_FILE"
}

edit_notes() {
  local ed="${VISUAL:-${EDITOR:-}}"
  if [[ -z "$ed" ]]; then if command -v nano >/dev/null; then ed=nano; else ed=vi; fi; fi
  [[ -s "$NOTES_FILE" ]] || printf '%s\n' "$PLACEHOLDER" > "$NOTES_FILE"
  # shellcheck disable=SC2086  # $ed may carry arguments ("code --wait")
  $ed "$NOTES_FILE" </dev/tty >/dev/tty
}

show_notes() {
  printf '\n%s── release notes ──%s\n' "$DIM" "$RESET" >&2
  sed 's/^/  /' "$NOTES_FILE" >&2
  printf '%s───────────────────%s\n' "$DIM" "$RESET" >&2
}

step "Release notes"
case "$NOTES_MODE" in
  claude) generate_with_claude || die "Claude produced no notes." ;;
  placeholder) printf '%s\n' "$PLACEHOLDER" > "$NOTES_FILE" ;;
  "") : ;;
  *) cp "$NOTES_MODE" "$NOTES_FILE" 2>/dev/null || [[ "$NOTES_MODE" -ef "$NOTES_FILE" ]] ;;
esac
if [[ -z "$NOTES_MODE" ]]; then
  while true; do
    menu "How should the release notes be written?" \
      "Generate with Claude (from commits since ${PREV:-the beginning})" \
      "Placeholder: \"$PLACEHOLDER\"" \
      "Write them myself in \$EDITOR (Markdown)"
    case $MENU_CHOICE in
      0) generate_with_claude || { warn "Claude failed or returned nothing."; continue; } ;;
      1) printf '%s\n' "$PLACEHOLDER" > "$NOTES_FILE" ;;
      2) edit_notes ;;
    esac
    accepted=0
    while true; do
      show_notes
      menu "Use these notes?" "Use them" "Edit in \$EDITOR" "Start over"
      case $MENU_CHOICE in
        0) accepted=1; break ;;
        1) edit_notes ;;
        2) break ;;
      esac
    done
    (( accepted )) && break
  done
fi
[[ -s "$NOTES_FILE" ]] || die "Release notes are empty."

# --- 3. build-number commit + tag ----------------------------------------------------------------------------
step "Tag $TAG"
# publish-win.sh bumps build-number.txt; commit it so the tagged commit holds the number the exes were built with.
if [[ -n "$(git status --porcelain -- build-number.txt)" ]]; then
  if confirm "Commit build-number.txt (build $BUILD) so the tag includes it?"; then
    run git add build-number.txt
    run git commit -m "Update build-number.txt" -- build-number.txt
  else
    warn "build-number.txt left uncommitted; the tag will not contain build $BUILD."
  fi
fi
HEAD_SHA="$(git rev-parse --short HEAD)"
OLD_LOCAL="$(git rev-parse -q --short --verify "refs/tags/$TAG^{commit}" 2>/dev/null || true)"
OLD_REMOTE="$(git ls-remote --tags "$REMOTE" "refs/tags/$TAG" "refs/tags/$TAG^{}" | tail -1 | cut -c1-7)"
HAS_RELEASE=0
gh release view "$TAG" >/dev/null 2>&1 && HAS_RELEASE=1

# --- 4. stage assets, confirm, release ------------------------------------------------------------------------
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
ASSETS=()
for rid in "${RIDS[@]}"; do
  cp "publish/$rid/NetManager.exe" "$STAGE/$(asset_name "$rid")"
  ASSETS+=("$STAGE/$(asset_name "$rid")")
done

step "Summary"
info "  release   $TAG (build $BUILD)$( [[ "$VERSION" == *-* ]] && echo ' — marked pre-release' ) on $REPO"
info "  commit    $HEAD_SHA"
[[ -n "$OLD_LOCAL" ]] && warn "local tag $TAG exists at $OLD_LOCAL — will be deleted and moved to $HEAD_SHA"
[[ -n "$OLD_REMOTE" ]] && warn "remote tag $TAG exists at $OLD_REMOTE — will be deleted and replaced"
(( HAS_RELEASE )) && warn "GitHub release $TAG exists — will be deleted and recreated"
info "  assets    $(for a in "${ASSETS[@]}"; do printf '%s (%s)  ' "$(basename "$a")" "$(du -h "$a" | cut -f1)"; done)"
confirm "Create the release?" || die "Cancelled (exes are built; notes are in $NOTES_FILE; rerun with --skip-publish --notes $NOTES_FILE)."

if (( HAS_RELEASE )); then run gh release delete "$TAG" --yes; fi
if [[ -n "$OLD_REMOTE" ]]; then run git push "$REMOTE" ":refs/tags/$TAG"; fi
if [[ -n "$OLD_LOCAL" ]]; then run git tag -d "$TAG"; fi
run git tag -a "$TAG" -m "NetManager $TAG" HEAD
run git push "$REMOTE" "refs/tags/$TAG"

PRE=()
[[ "$VERSION" == *-* ]] && PRE=(--prerelease)
run gh release create "$TAG" --verify-tag --title "NetManager $TAG (build $BUILD)" --notes-file "$NOTES_FILE" ${PRE[@]+"${PRE[@]}"} "${ASSETS[@]}" \
  || die "Release creation failed. The tag is already pushed; retry: tools/release.sh --skip-publish --notes $NOTES_FILE"

step "Done"
if (( DRY_RUN )); then info "Dry run — nothing was changed."; else
  printf '%s✔ %s%s\n' "$GREEN" "$(gh release view "$TAG" --json url -q .url)" "$RESET" >&2
fi
if [[ -n "$(git rev-list '@{u}..HEAD' 2>/dev/null)" ]]; then warn "Current branch has commits not on the remote yet (git push)."; fi
