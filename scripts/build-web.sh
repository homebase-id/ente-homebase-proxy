#!/usr/bin/env bash
# Builds the Ente Photos web app into client/photos, where the proxy serves it
# (src/EnteHomebaseProxy/WebApp.cs), calling the API on the same site under /api.
#
# Builds the Ente checkout in ENTE_REPO (default: ../ente) as it is, and warns
# if that isn't the commit in ente.ref, the one the deployed image is built from.
# Needs Node 24 and Rust with the wasm32-unknown-unknown target.
set -euo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"
ente="${ENTE_REPO:-$here/../ente}"
pinned="$(tr -d '[:space:]' <"$here/ente.ref")"

if [[ ! -f "$ente/web/packages/homebase/constants.ts" ]]; then
  echo "No Ente checkout at $ente: set ENTE_REPO, or clone homebase-id/homebase-ente as ../ente" >&2
  exit 1
fi

head="$(git -C "$ente" rev-parse HEAD)"
if [[ "$head" != "$pinned" ]]; then
  echo "warning: $ente is at $head, ente.ref pins $pinned" >&2
fi

cd "$ente/web"
[[ -d node_modules ]] || npm ci
NEXT_PUBLIC_ENTE_HOMEBASE_PROXY=/api npm run build:photos

rm -rf "$here/client/photos"
mkdir -p "$here/client"
cp -R apps/photos/out "$here/client/photos"
echo "Built $head into client/photos"
