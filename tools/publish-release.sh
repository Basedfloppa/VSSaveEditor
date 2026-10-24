#!/usr/bin/env bash
# Build the same standalone single-file assets as .github/workflows/release.yml.
#
# Usage:
#   tools/publish-release.sh
#   VERSION=0.2.0 RIDS="linux-x64" tools/publish-release.sh
#
# The output is intentionally ignored by git. The Windows runtime pack can be restored from
# Linux; the produced .exe is not executed here, so the release workflow also publishes it on
# windows-latest and checks the PE header.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${VERSION:-0.1.0}"
output="${OUT:-$root/artifacts/release}"
rids="${RIDS:-linux-x64 win-x64}"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "VERSION must be semver without the leading v, got '$version'" >&2
  exit 2
fi

mkdir -p "$output"

# Use the in-tree restore caches when present; this checkout may run on a host whose home
# directory is read-only. An explicitly set environment variable always wins.
if [[ -z "${NUGET_PACKAGES:-}" && -d "$root/.packages" ]]; then
  export NUGET_PACKAGES="$root/.packages"
fi
if [[ -z "${NUGET_HTTP_CACHE_PATH:-}" && -d "$root/.httpcache" ]]; then
  export NUGET_HTTP_CACHE_PATH="$root/.httpcache"
fi

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

for rid in $rids; do
  case "$rid" in
    linux-*) ext="" ;;
    win-*) ext=".exe" ;;
    *) echo "Unsupported RID '$rid' (expected linux-* or win-*)" >&2; exit 2 ;;
  esac

  publish_dir="$output/publish-$rid"
  rm -rf "$publish_dir"
  mkdir -p "$publish_dir"

  dotnet publish "$root/VSSaveEditor/VSSaveEditor.csproj" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=false \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:Version="$version" \
    -p:ContinuousIntegrationBuild=true \
    -o "$publish_dir"

  files=("$publish_dir"/*)
  if [[ "${#files[@]}" -ne 1 || ! -f "${files[0]}" ]]; then
    echo "Expected exactly one published file in $publish_dir, got: ${files[*]}" >&2
    exit 1
  fi

  expected="VSSaveEditor$ext"
  if [[ "$(basename "${files[0]}")" != "$expected" ]]; then
    echo "Unexpected published file '$(basename "${files[0]}")' (expected '$expected')" >&2
    exit 1
  fi

  binary="${files[0]}"
  bytes="$(wc -c < "$binary")"
  if [[ "$bytes" -lt 20971520 ]]; then
    echo "Published file is unexpectedly small: $bytes bytes" >&2
    exit 1
  fi

  magic="$(od -An -tx1 -N4 "$binary" | tr -d ' \n')"
  case "$rid" in
    linux-*) [[ "$magic" == "7f454c46" ]] || { echo "Not an ELF executable: $binary" >&2; exit 1; } ;;
    win-*)   [[ "$magic" == "4d5a"* ]] || { echo "Not a PE executable: $binary" >&2; exit 1; } ;;
  esac

  asset="$output/VSSaveEditor-$version-$rid$ext"
  cp -f "$binary" "$asset"
  if [[ "$rid" == linux-* ]]; then
    chmod +x "$asset"
  fi

  hash="$(sha256 "$asset")"
  printf '%s  %s\n' "$hash" "$(basename "$asset")" > "$asset.sha256"
  echo "Published $(basename "$asset") ($bytes bytes, sha256 $hash)"
done
