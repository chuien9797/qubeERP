$ErrorActionPreference = 'Stop'
$envPath = Join-Path $PSScriptRoot '..\.env'
[string[]]$lines = @(if (Test-Path -LiteralPath $envPath) { Get-Content -LiteralPath $envPath })
$changed = $false

foreach ($key in 'MSSQL_SA_PASSWORD', 'PRODUCT_DB_PASSWORD', 'INVENTORY_DB_PASSWORD', 'ORDER_DB_PASSWORD', 'RABBITMQ_PASSWORD') {
    $index = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].StartsWith($key + '=')) { $index = $i; break }
    }
    if ($index -ge 0 -and -not [string]::IsNullOrWhiteSpace($lines[$index].Substring($key.Length + 1))) {
        continue
    }

    $bytes = New-Object byte[] 24
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    $password = 'Qube!' + [BitConverter]::ToString($bytes).Replace('-', '')
    if ($index -ge 0) { $lines[$index] = "$key=$password" } else { $lines += "$key=$password" }
    $changed = $true
}

if ($changed) {
    [IO.File]::WriteAllLines($envPath, [string[]]$lines)
    Write-Output 'Generated missing local credentials in .env. Existing credentials were preserved.'
}
else {
    Write-Output 'Local credentials are already configured.'
}
