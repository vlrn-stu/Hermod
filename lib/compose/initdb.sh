#!/usr/bin/env bash
# Postgres init: creates hermod database + per-app roles.
# Runs once on first container start (postgres:17-alpine entrypoint hook).
set -euo pipefail

psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" <<-SQL
    CREATE ROLE hermod_app LOGIN PASSWORD '${HERMOD_APP_PASSWORD}';
    CREATE ROLE vault_mig  LOGIN CREATEROLE PASSWORD '${VAULT_APP_PASSWORD}';
    CREATE ROLE vault_app  LOGIN PASSWORD '${VAULT_APP_PASSWORD}';

    CREATE DATABASE hermod OWNER hermod_app;
    GRANT ALL PRIVILEGES ON DATABASE hermod TO hermod_app;

    GRANT ALL PRIVILEGES ON DATABASE vault TO vault_mig;
    GRANT ALL PRIVILEGES ON DATABASE vault TO vault_app;
SQL

psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d hermod <<-SQL
    GRANT ALL ON SCHEMA public TO hermod_app;
SQL

psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d vault <<-SQL
    GRANT ALL ON SCHEMA public TO vault_mig;
    GRANT ALL ON SCHEMA public TO vault_app;
SQL
