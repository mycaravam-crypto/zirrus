#!/bin/sh
# Gives the application role zirrus_app (created by the AuditHardening migration) its login password.
# Runs after the migrations on every deployment (db-roles service), so new installations, existing
# volumes and password rotation are all handled. The API and worker connect as zirrus_app: DML on
# application tables, but only INSERT and SELECT on the audit tables (security.md, Q10).
set -eu
export PGHOST=postgres PGDATABASE=zirrus PGUSER=zirrus
PGPASSWORD="$(cat /run/secrets/postgres_password)"
export PGPASSWORD
APP_PASSWORD="$(cat /run/secrets/app_db_password)"
# The password is passed as a psql variable, quoted by psql (:'pw'), never interpolated into SQL text.
psql -v ON_ERROR_STOP=1 -v pw="$APP_PASSWORD" -q <<'SQL'
SELECT count(*) AS role_exists FROM pg_roles WHERE rolname = 'zirrus_app' \gset
\if :role_exists
\else
  \echo 'zirrus_app does not exist: run the migrations first'
  \quit 1
\endif
ALTER ROLE zirrus_app WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'pw';
SQL
echo "zirrus_app login configured"
