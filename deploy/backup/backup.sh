#!/usr/bin/env bash
# Consistent backup checkpoint of a ZIRRUS Compose deployment (#50, backup-restore.md).
#
#   deploy/backup/backup.sh <output-directory>
#
# Stops the API and worker so no asset, rendition, purge or audit write happens during the checkpoint,
# then writes into <output-directory>:
#   db.dump        PostgreSQL custom-format dump (owner account)
#   blobs.tar      the asset store volume (originals and renditions)
#   manifest.tsv   every blob the database references: kind, asset ID, key, SHA-256, size (sorted by key)
#   backup.json    time, last migration, counts
#   SHA256SUMS     checksums of the four files above
# Every manifest entry is verified against the live volume before the API and worker restart.
# The output is plaintext: encrypt it and copy it off-host as described in backup-restore.md.
# COMPOSE_FILES overrides the Compose files (default: deploy/compose.yaml).
set -euo pipefail

out="${1:?usage: backup.sh <output-directory>}"
here="$(cd "$(dirname "$0")/.." && pwd)"
read -r -a files <<< "${COMPOSE_FILES:-$here/compose.yaml}"
compose=(docker compose); for f in "${files[@]}"; do compose+=(-f "$f"); done
project="$("${compose[@]}" config --format json | python3 -c 'import json,sys; print(json.load(sys.stdin)["name"])')"
helper="$("${compose[@]}" config --images | grep -m1 '^postgres:')"
blobs_volume="${project}_blobs"

mkdir -p "$out"
if [[ -n "$(ls -A "$out")" ]]; then
  echo "backup: $out is not empty" >&2
  exit 1
fi

psql() { "${compose[@]}" exec -T postgres psql -U zirrus -d zirrus -v ON_ERROR_STOP=1 -At "$@"; }

restart() { "${compose[@]}" start api worker > /dev/null; }
echo "backup: stopping api and worker"
"${compose[@]}" stop api worker > /dev/null
trap restart EXIT

started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
"${compose[@]}" exec -T postgres pg_dump -U zirrus -d zirrus -Fc > "$out/db.dump"

"$here/backup/manifest.sh" "${compose[@]}" > "$out/manifest.tsv"

migration="$(psql -c 'SELECT max("MigrationId") FROM "__EFMigrationsHistory"')"
assets="$(psql -c 'SELECT count(*) FROM assets')"
audit_events="$(psql -c 'SELECT count(*) FROM audit_events')"

docker run --rm --user 0 -v "$blobs_volume:/blobs:ro" "$helper" tar -C /blobs --exclude ./.tmp -cf - . > "$out/blobs.tar"

# Verify every referenced blob in the volume before restarting: a backup of a corrupt store is useless.
docker run --rm -i --user 0 -v "$blobs_volume:/blobs:ro" "$helper" sh -c '
  cd /blobs
  failed=0
  while IFS="$(printf "\t")" read -r kind asset key sha size; do
    actual="$(sha256sum "$key" 2>/dev/null | cut -d" " -f1)"
    if [ "$actual" != "$sha" ]; then echo "backup: checksum mismatch for $kind of asset $asset ($key)" >&2; failed=1; fi
  done
  exit $failed' < "$out/manifest.tsv"

blobs_count="$(wc -l < "$out/manifest.tsv" | tr -d ' ')"
python3 - "$out/backup.json" "$started" "$migration" "$assets" "$audit_events" "$blobs_count" <<'PY'
import json, sys
path, started, migration, assets, audit, blobs = sys.argv[1:]
json.dump({"format": 1, "startedAt": started, "lastMigration": migration, "assets": int(assets),
           "auditEvents": int(audit), "manifestBlobs": int(blobs)}, open(path, "w"), indent=2)
PY
(cd "$out" && sha256sum db.dump blobs.tar manifest.tsv backup.json > SHA256SUMS)
echo "backup: $blobs_count blobs and $assets assets verified, written to $out"
