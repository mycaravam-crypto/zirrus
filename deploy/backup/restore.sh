#!/usr/bin/env bash
# Restores a backup made by backup.sh into a fresh ZIRRUS Compose deployment (#50, backup-restore.md).
#
#   deploy/backup/restore.sh <backup-directory>
#
# Requires deploy/secrets/ (the same or new secrets) and no existing pgdata or blobs volume, so a restore
# never overwrites data. It checks SHA256SUMS before touching anything, restores the database and the
# asset store, verifies every manifest entry's SHA-256 and the row counts recorded at backup time, then
# starts the full stack (migrations run and db-roles sets the application login as on any deployment).
# Pending jobs resume from the restored queue; renditions missing from the backup can be rebuilt by retry.
# COMPOSE_FILES overrides the Compose files (default: deploy/compose.yaml).
set -euo pipefail

backup="$(cd "${1:?usage: restore.sh <backup-directory>}" && pwd)"
here="$(cd "$(dirname "$0")/.." && pwd)"
read -r -a files <<< "${COMPOSE_FILES:-$here/compose.yaml}"
compose=(docker compose); for f in "${files[@]}"; do compose+=(-f "$f"); done
project="$("${compose[@]}" config --format json | python3 -c 'import json,sys; print(json.load(sys.stdin)["name"])')"
helper="$("${compose[@]}" config --images | grep -m1 '^postgres:')"
blobs_volume="${project}_blobs"
started=$SECONDS

echo "restore: checking backup checksums"
(cd "$backup" && sha256sum --quiet -c SHA256SUMS)
expected() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]])' "$backup/backup.json" "$1"; }

for volume in "${project}_pgdata" "$blobs_volume"; do
  if docker volume inspect "$volume" > /dev/null 2>&1; then
    echo "restore: volume $volume exists; restore only into a fresh deployment (docker compose down -v first, after checking)" >&2
    exit 1
  fi
done

psql() { "${compose[@]}" exec -T postgres psql -U zirrus -d zirrus -v ON_ERROR_STOP=1 -At "$@"; }

echo "restore: database"
"${compose[@]}" up -d --wait postgres > /dev/null
# The application role is cluster-wide, so the dump only carries its grants; create it first (no login yet).
psql -c "DO \$\$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'zirrus_app') THEN CREATE ROLE zirrus_app NOLOGIN; END IF; END \$\$;" > /dev/null
"${compose[@]}" exec -T postgres pg_restore -U zirrus -d zirrus --exit-on-error < "$backup/db.dump"

echo "restore: asset store"
docker volume create "$blobs_volume" > /dev/null
docker run --rm -i --user 0 -v "$blobs_volume:/blobs" "$helper" tar -C /blobs -xf - < "$backup/blobs.tar"

echo "restore: verifying originals and renditions"
docker run --rm -i --user 0 -v "$blobs_volume:/blobs:ro" "$helper" sh -c '
  cd /blobs
  failed=0; count=0
  while IFS="$(printf "\t")" read -r kind asset key sha size; do
    count=$((count + 1))
    actual="$(sha256sum "$key" 2>/dev/null | cut -d" " -f1)"
    if [ "$actual" != "$sha" ]; then echo "restore: checksum mismatch for $kind of asset $asset ($key)" >&2; failed=1; fi
  done
  echo "restore: $count blobs checked"
  exit $failed' < "$backup/manifest.tsv"

for check in "assets:SELECT count(*) FROM assets" "auditEvents:SELECT count(*) FROM audit_events"; do
  name="${check%%:*}"; actual="$(psql -c "${check#*:}")"
  if [[ "$actual" != "$(expected "$name")" ]]; then
    echo "restore: $name is $actual, backup recorded $(expected "$name")" >&2
    exit 1
  fi
done

# The manifest must still describe the restored database exactly (same query as backup.sh).
"$here/backup/manifest.sh" "${compose[@]}" | cmp -s - "$backup/manifest.tsv" \
  || { echo "restore: restored database does not match the backup manifest" >&2; exit 1; }

echo "restore: starting the stack"
"${compose[@]}" up -d --wait --wait-timeout 600 > /dev/null
echo "restore: done in $((SECONDS - started)) s (backup from $(expected startedAt), last migration $(expected lastMigration))"
