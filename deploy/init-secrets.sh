#!/usr/bin/env bash
# Creates the Docker secret files for deploy/compose.yaml. Existing files are kept.
# The files live in deploy/secrets/ (git-ignored) with mode 600.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p secrets
chmod 700 secrets
if [[ ! -s secrets/postgres_password ]]; then
  (umask 077; openssl rand -base64 33 | tr -d '\n/+=' > secrets/postgres_password)
fi
if [[ ! -s secrets/zirrus_db_connection ]]; then
  (umask 077; printf 'Host=postgres;Database=zirrus;Username=zirrus;Password=%s' "$(cat secrets/postgres_password)" > secrets/zirrus_db_connection)
fi
if [[ ! -s secrets/app_db_password ]]; then
  # The API and worker connect as zirrus_app (no DDL; insert-only on the audit tables, Q10).
  (umask 077; openssl rand -base64 33 | tr -d '\n/+=' > secrets/app_db_password)
fi
if [[ ! -s secrets/zirrus_app_db_connection ]]; then
  (umask 077; printf 'Host=postgres;Database=zirrus;Username=zirrus_app;Password=%s' "$(cat secrets/app_db_password)" > secrets/zirrus_app_db_connection)
fi
if [[ ! -s secrets/audit_digest_key ]]; then
  # 256-bit HMAC key that signs the audit digests (security.md, Q10). Keep a copy off-host to verify them.
  (umask 077; openssl rand -base64 32 | tr -d '\n' > secrets/audit_digest_key)
fi
if [[ ! -s secrets/dataprotection_key ]]; then
  # 256-bit key that encrypts the Data Protection key ring at rest (security.md).
  (umask 077; openssl rand -base64 32 | tr -d '\n' > secrets/dataprotection_key)
fi
# The containers run as non-root users; secret files must be readable inside them.
chmod 644 secrets/postgres_password secrets/zirrus_db_connection secrets/dataprotection_key \
  secrets/app_db_password secrets/zirrus_app_db_connection secrets/audit_digest_key
echo "secrets ready in deploy/secrets/"
