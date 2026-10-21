set -euo pipefail

for name in PRODUCT_DB_PASSWORD INVENTORY_DB_PASSWORD ORDER_DB_PASSWORD; do
    value="${!name}"
    if [[ ! "$value" =~ ^[A-Za-z0-9!_-]{32,128}$ ]]; then
        printf 'Invalid database credential format. Run scripts/Initialize-LocalEnvironment.ps1.\n' >&2
        exit 1
    fi
done

export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
/opt/mssql-tools18/bin/sqlcmd -S sqlserver -U sa -C -b -i /setup/Initialize-Databases.sql
