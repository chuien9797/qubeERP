set -euo pipefail

for name in PRODUCT_DB_PASSWORD INVENTORY_DB_PASSWORD ORDER_DB_PASSWORD; do
    value="${!name}"
    if [[ ! "$value" =~ ^[A-Za-z0-9!_-]{32,128}$ ]]; then
        printf 'Invalid database credential format. Run scripts/Initialize-LocalEnvironment.ps1.\n' >&2
        exit 1
    fi
done

export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
for attempt in {1..12}; do
    if output=$(/opt/mssql-tools18/bin/sqlcmd -S sqlserver -d master -U sa -C -b -i /setup/Initialize-Databases.sql 2>&1); then
        if [[ -n "$output" ]]; then printf '%s\n' "$output"; fi
        exit 0
    fi

    printf '%s\n' "$output" >&2
    if [[ "$output" != *"Msg 904,"* || "$attempt" -eq 12 ]]; then
        exit 1
    fi

    printf 'SQL database recovery is still in progress; retrying setup in 5 seconds (%s/12).\n' "$attempt" >&2
    sleep 5
done
