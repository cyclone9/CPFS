#!/usr/bin/env bash
# Run by .github/workflows/release.yml from the repository root, at the tagged commit.
# Usage: .github/scripts/release-check.sh check X.Y.Z
#          Fails unless the four READMEs show exactly X.Y.Z in their version row, CHANGELOG.md has a
#          non-empty "## X.Y.Z" section, Directory.Build.props sets VersionPrefix X.Y.Z, and no project
#          file sets another Version, VersionPrefix or a VersionSuffix.
#        .github/scripts/release-check.sh notes X.Y.Z <sha256-file>
#          Prints the release notes: the "## X.Y.Z" section of CHANGELOG.md, then the checksum line.
set -euo pipefail

mode="${1:?usage: release-check.sh check|notes X.Y.Z [sha256-file]}"
ver="${2:?usage: release-check.sh check|notes X.Y.Z [sha256-file]}"

if ! [[ "$ver" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "::error::$ver is not MAJOR.MINOR.PATCH. Releases are published as Latest, so a pre-release suffix is refused."
  exit 1
fi
esc="${ver//./\\.}"

# The CHANGELOG section body: after the "## X.Y.Z" heading (a date may follow), up to the next "## ".
section() {
  awk -v h="## $ver" '
    { sub(/\r$/, "") }
    index($0, "## ") == 1 { if (on) exit; if ($0 == h || index($0, h " ") == 1) { on = 1; next } }
    on { lines[++n] = $0 }
    END {
      s = 1; while (s <= n && lines[s] ~ /^[[:space:]]*$/) s++
      e = n; while (e >= s && lines[e] ~ /^[[:space:]]*$/) e--
      for (i = s; i <= e; i++) print lines[i]
    }' CHANGELOG.md
}

prop() { sed -nE "s#.*<$1>[[:space:]]*([^<[:space:]]*)[[:space:]]*</$1>.*#\\1#p" "$2" | head -1; }

case "$mode" in
  check)
    fail=0
    err() { echo "::error::$1"; fail=1; }
    # RTL READMEs may carry a direction mark (U+200E, U+200F) beside the value.
    marks=$'\xe2\x80\x8e\xe2\x80\x8f'
    row="^\|[^|]*\|[[:space:]$marks]*${esc}[[:space:]$marks]*\|[[:space:]]*$"
    for f in README.md README.fa.md README.ar.md README.tr.md; do
      if [ ! -f "$f" ]; then err "$f is missing"; continue; fi
      head -20 "$f" | tr -d '\r' | grep -Eq "$row" \
        || err "$f: no version row showing exactly $ver in its first 20 lines (drop the \"in development\" note at a release)"
    done
    if [ -z "$(section)" ]; then err "CHANGELOG.md has no \"## $ver\" section, or it is empty"; fi
    [ -f Directory.Build.props ] || err "Directory.Build.props is missing"
    [ "$(prop VersionPrefix Directory.Build.props 2>/dev/null || true)" = "$ver" ] \
      || err "Directory.Build.props: VersionPrefix is not $ver"
    while IFS= read -r p; do
      [ -n "$p" ] || continue
      v="$(prop Version "$p")"; vp="$(prop VersionPrefix "$p")"; vs="$(prop VersionSuffix "$p")"
      [ -z "$v" ] || [ "$v" = "$ver" ] || err "$p sets <Version>$v</Version>"
      [ -z "$vp" ] || [ "$vp" = "$ver" ] || err "$p sets <VersionPrefix>$vp</VersionPrefix>"
      [ -z "$vs" ] || err "$p still sets <VersionSuffix>$vs</VersionSuffix>; a release has none"
    done < <(git ls-files | grep -E '(\.csproj|(^|/)Directory\.Build\.props)$' || true)
    [ "$fail" -eq 0 ] || exit 1
    echo "Release files agree on $ver"
    ;;
  notes)
    sumfile="${3:?usage: release-check.sh notes X.Y.Z <sha256-file>}"
    sum="$(tr -d '\r' < "$sumfile")"
    if ! [[ "$sum" =~ ^[0-9a-f]{64}\ \ Cpfs-$esc\.exe$ ]]; then
      echo "::error::$sumfile does not hold one \"<sha256>  Cpfs-$ver.exe\" line"
      exit 1
    fi
    body="$(section)"
    if [ -z "$body" ]; then echo "::error::CHANGELOG.md has no \"## $ver\" section"; exit 1; fi
    # shellcheck disable=SC2016 # the backticks are a Markdown code fence
    printf '%s\n\nSHA-256:\n\n```\n%s\n```\n' "$body" "$sum"
    ;;
  *)
    echo "usage: release-check.sh check|notes X.Y.Z [sha256-file]" >&2
    exit 1
    ;;
esac
