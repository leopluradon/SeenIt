#!/usr/bin/env bash
# Read-only check that a deployment is healthy. Creates nothing.
set -euo pipefail

BASE="${1:-http://192.168.1.167:8082}"
fail=0

check() {
    local label="$1" url="$2" expect="${3:-}" want="${4:-200}"
    local body status
    body=$(curl -sS --max-time 15 -w '\n%{http_code}' "$url") || { echo "FAIL  $label (request failed)"; fail=1; return; }
    status=$(tail -n1 <<<"$body")
    if [ "$status" != "$want" ]; then echo "FAIL  $label (HTTP $status, wanted $want)"; fail=1; return; fi
    if [ -n "$expect" ] && ! grep -q -- "$expect" <<<"$body"; then echo "FAIL  $label (missing \"$expect\")"; fail=1; return; fi
    echo "ok    $label"
}

echo "Checking $BASE"
check "health"            "$BASE/healthz"                                  "ok"
check "app shell"         "$BASE/"                                         "SeenIt"
check "deep link shell"   "$BASE/movie/603"                                "SeenIt"
check "stylesheet"        "$BASE/_content/SeenIt.UI/seenit.css"            "--accent"
check "manifest"          "$BASE/manifest.webmanifest"                     "SeenIt"
check "TMDB trending"     "$BASE/api/catalog/browse/Movie/trending/week"   "tmdbId"
check "TMDB movie"        "$BASE/api/catalog/movie/603"                    "The Matrix"
check "auth required"     "$BASE/api/history"                              "" 401
check "unknown api 404"   "$BASE/api/nope"                                 "" 404

asset=$(curl -sS --max-time 10 "$BASE/" | grep -oE '_framework/blazor\.webassembly[^"]*\.js' | head -1 || true)
if [ -n "$asset" ]; then check "blazor runtime" "$BASE/$asset"; else echo "FAIL  blazor runtime (not referenced by the page)"; fail=1; fi

[ "$fail" -eq 0 ] && echo "All checks passed." || echo "Some checks failed."
exit "$fail"
