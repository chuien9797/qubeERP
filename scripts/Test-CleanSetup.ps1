param(
    [ValidateRange(1024, 65535)][int]$ProductPort = 15101,
    [ValidateRange(1024, 65535)][int]$InventoryPort = 15102,
    [ValidateRange(1024, 65535)][int]$OrderPort = 15103,
    [ValidateRange(1024, 65535)][int]$SqlPort = 14331,
    [ValidateRange(1024, 65535)][int]$RabbitPort = 5673,
    [ValidateRange(1024, 65535)][int]$RabbitManagementPort = 15673
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run = 'stage7-' + [Guid]::NewGuid().ToString('N')
$snapshot = Join-Path $root ".appdata\$run"
$variables = @{
    COMPOSE_PROJECT_NAME = "qubeerp-$run"
    PRODUCT_PORT = "$ProductPort"
    INVENTORY_PORT = "$InventoryPort"
    ORDER_PORT = "$OrderPort"
    SQL_PORT = "$SqlPort"
    RABBITMQ_PORT = "$RabbitPort"
    RABBITMQ_MANAGEMENT_PORT = "$RabbitManagementPort"
}
$previous = @{}
$started = $false
Push-Location $root
try {
    $files = @(git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot list repository source files.' }
    $files += 'README.md'
    $files += @(Get-ChildItem (Join-Path $root 'docs') -Recurse -File | ForEach-Object {
        $_.FullName.Substring($root.Length + 1)
    })
    foreach ($file in ($files | Sort-Object -Unique)) {
        $destination = Join-Path $snapshot $file
        $null = New-Item -ItemType Directory -Path (Split-Path $destination) -Force
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $destination
    }
    foreach ($key in $variables.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $variables[$key], 'Process')
    }
    Set-Location $snapshot
    Write-Output "Clean working-source snapshot: $snapshot"
    Write-Output "Isolated Compose project: $env:COMPOSE_PROJECT_NAME"
    & .\scripts\Initialize-LocalEnvironment.ps1
    docker compose config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Clean-source Compose configuration failed.' }
    $started = $true
    docker compose up --build --detach --wait --wait-timeout 300
    if ($LASTEXITCODE -ne 0) { throw 'Clean-source Compose startup failed.' }
    & .\scripts\Test-Compose.ps1 -ProductPort $ProductPort -InventoryPort $InventoryPort -OrderPort $OrderPort
    & .\scripts\Test-Orders.ps1 -ProductPort $ProductPort -InventoryPort $InventoryPort -OrderPort $OrderPort
    Write-Output 'Fresh-database startup, readiness, database isolation and multi-item order checks passed.'
}
finally {
    if ($started) {
        docker compose stop
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Could not stop every isolated validation container.' }
    }
    foreach ($key in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
    }
    Pop-Location
    Write-Output "Snapshot and validation volumes retained. Original demo containers and data were not reset: $snapshot"
}
