$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    docker compose exec -T sqlserver test -x /opt/mssql-tools18/bin/sqlcmd
    if ($LASTEXITCODE -ne 0) { throw 'The selected SQL image does not contain the expected sqlcmd executable.' }
    $query = "SET NOCOUNT ON; SELECT 1; IF (SELECT COUNT(*) FROM sys.databases WHERE name IN ('ProductDb', 'InventoryDb', 'OrderDb') AND state_desc = 'ONLINE') <> 3 THROW 50000, 'Expected three online service databases.', 1; SELECT name FROM sys.databases WHERE name IN ('ProductDb', 'InventoryDb', 'OrderDb');"
    $query | docker compose exec -T sqlserver bash -c 'SQLCMDPASSWORD=$MSSQL_SA_PASSWORD /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b'
    if ($LASTEXITCODE -ne 0) { throw 'SQL readiness query failed.' }
    foreach ($service in @(
        @{ Login = 'qube_product'; Database = 'ProductDb'; Others = @('InventoryDb', 'OrderDb') },
        @{ Login = 'qube_inventory'; Database = 'InventoryDb'; Others = @('ProductDb', 'OrderDb') },
        @{ Login = 'qube_order'; Database = 'OrderDb'; Others = @('ProductDb', 'InventoryDb') }
    )) {
        $query = "EXECUTE AS LOGIN = '$($service.Login)'; IF IS_SRVROLEMEMBER('sysadmin') <> 0 OR HAS_DBACCESS('$($service.Database)') <> 1 OR COALESCE(HAS_DBACCESS('$($service.Others[0])'), 0) <> 0 OR COALESCE(HAS_DBACCESS('$($service.Others[1])'), 0) <> 0 THROW 50000, 'Service database isolation failed.', 1; REVERT;"
        $query | docker compose exec -T sqlserver bash -c 'SQLCMDPASSWORD=$MSSQL_SA_PASSWORD /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b'
        if ($LASTEXITCODE -ne 0) { throw "Database access check failed for $($service.Login)." }
        Write-Output "$($service.Login): own database accessible; other service databases blocked; not sysadmin"
    }
    foreach ($port in 5101, 5102, 5103) {
        $base = "http://localhost:$port"
        $health = Invoke-WebRequest "$base/health" -UseBasicParsing
        if ($health.StatusCode -ne 200 -or $health.Content -ne 'Healthy') { throw "Health failed: $base" }
        $swagger = Invoke-WebRequest "$base/swagger/index.html" -UseBasicParsing
        if ($swagger.StatusCode -ne 200) { throw "Swagger UI failed: $base" }
        $schema = Invoke-RestMethod "$base/swagger/v1/swagger.json"
        if (-not $schema.openapi) { throw "OpenAPI document missing: $base" }
        Write-Output "$base health=200 Healthy; Swagger UI=200; OpenAPI=$($schema.openapi)"
    }
}
finally {
    Pop-Location
}
