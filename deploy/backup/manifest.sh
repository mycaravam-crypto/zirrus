#!/usr/bin/env bash
# Prints the backup manifest of a running deployment (#50): every blob the database references, as
# kind, asset ID, key, SHA-256 and size, tab-separated and sorted by key, so the same data always gives
# the same bytes. Originals of assets that still own them (soft-deleted ones keep their blobs until the
# purge) and their renditions. Used by backup.sh and restore.sh.
#
#   deploy/backup/manifest.sh docker compose -f deploy/compose.yaml [-f ...]
set -euo pipefail
"$@" exec -T postgres psql -U zirrus -d zirrus -v ON_ERROR_STOP=1 -At -F $'\t' <<'SQL'
SELECT kind, asset_id, key, sha256, size_bytes FROM (
  SELECT 'original' AS kind, a.id AS asset_id, a.original_key AS key, a.sha256, a.size_bytes
  FROM assets a WHERE a.state IN ('Quarantined', 'Processing', 'Active', 'Rejected', 'Failed', 'Deleting')
  UNION ALL
  SELECT r.kind, r.asset_id, r.storage_key, r.sha256, r.size_bytes
  FROM renditions r JOIN assets a ON a.catalog_id = r.catalog_id AND a.id = r.asset_id
  WHERE a.state IN ('Quarantined', 'Processing', 'Active', 'Rejected', 'Failed', 'Deleting')
) blobs ORDER BY key COLLATE "C"
SQL
