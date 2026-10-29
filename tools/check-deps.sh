#!/usr/bin/env bash
# P0-11: dependency check for CI.
#   1) lock files (packages.lock.json) match the actually resolved graph;
#   2) no known vulnerabilities among direct and transitive packages (NU1903).
#
# The global NuGet cache may be read-only (sandbox) — keep the cache in the repository:
#   NUGET_PACKAGES=$PWD/.packages NUGET_HTTP_CACHE_PATH=$PWD/.httpcache tools/check-deps.sh
set -euo pipefail
cd "$(dirname "$0")/.."

SOLUTION=VSSaveEditor.sln
# .uisnap and tools/scope-probe — projects outside the solution, each with its own lock file
PROJECTS=("$SOLUTION" .uisnap/uisnap.csproj tools/scope-probe/scope-probe.csproj)

echo "== restore --locked-mode (lock files are up to date) =="
for p in "${PROJECTS[@]}"; do
  dotnet restore "$p" --locked-mode
done

echo
echo "== vulnerable packages (direct and transitive) =="
out=$(dotnet list "$SOLUTION" package --vulnerable --include-transitive)
echo "$out"

# `dotnet list` prints findings but returns 0 — so we check the text.
if grep -q "has the following vulnerable packages" <<<"$out"; then
  echo "FAIL: vulnerable packages found — update versions or add a direct PackageReference" >&2
  exit 1
fi

# Fail closed when the vulnerability feed could not be queried: an empty result
# is not evidence of a clean dependency graph.
if grep -q "NU1900" <<<"$out"; then
  echo "FAIL: vulnerability data could not be fetched (NU1900) — rerun with network access" >&2
  exit 1
fi

echo "OK: lock files are up to date, no vulnerable packages"
