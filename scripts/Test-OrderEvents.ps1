$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(30)
$projectRoot = Split-Path -Parent $PSScriptRoot
$started = [DateTimeOffset]::UtcNow.ToString('o')

function Invoke-EventCompose {
    param([string[]]$Arguments)
    & docker compose --project-directory $projectRoot --file "$projectRoot\docker-compose.yml" @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Compose failed: $Arguments" }
}

function Send-EventJson {
    param([string]$Method, [string]$Url, $Body, [int]$ExpectedStatus = 200, [string]$Key)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Url)
    if ($null -ne $Body) {
        $request.Content = [System.Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 8), [Text.Encoding]::UTF8, 'application/json')
    }
    if ($Key) { $request.Headers.Add('Idempotency-Key', $Key) }
    try {
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne $ExpectedStatus) { throw "Expected $ExpectedStatus for $Method $Url; received $($response.StatusCode): $json" }
            return ($json | ConvertFrom-Json)
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function Read-EventCount {
    param([string]$Predicate)
    $query = "SET NOCOUNT ON; SELECT COUNT(*) FROM OrderDb.dbo.OutboxMessages WHERE $Predicate;"
    $output = $query | docker compose --project-directory $projectRoot --file "$projectRoot\docker-compose.yml" exec -T sqlserver bash -c 'SQLCMDPASSWORD=$MSSQL_SA_PASSWORD /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -h -1 -W'
    if ($LASTEXITCODE -ne 0) { throw 'Outbox verification query failed.' }
    return [int](($output | Out-String).Trim())
}

function Wait-EventCount {
    param([string]$Predicate, [int]$Expected, [int]$Seconds = 120)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        if ((Read-EventCount $Predicate) -eq $Expected) { return }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Outbox did not reach the expected count $Expected."
}

try {
    Invoke-EventCompose @('up', '--detach', '--wait', '--wait-timeout', '180', 'rabbitmq', 'order-event-worker', 'order-service')
    $product = Send-EventJson 'POST' 'http://localhost:5101/api/products' @{
        sku = 'events-' + [Guid]::NewGuid().ToString('N'); name = 'Order events demo'; unitPrice = 12.50
    } 201
    $inventory = "http://localhost:5102/api/inventory/$($product.id)"
    $null = Send-EventJson 'PUT' $inventory @{ onHand = 10; reason = 'Order events demo setup' }
    $warm = Send-EventJson 'POST' 'http://localhost:5103/api/orders' @{
        items = @(@{ productId = $product.id; quantity = 1 })
    } 201 ('warm-' + [Guid]::NewGuid().ToString('N'))
    Wait-EventCount "AggregateId = '$([Guid]$warm.orderId)' AND PublishedAtUtc IS NOT NULL" 1

    Invoke-EventCompose @('stop', 'rabbitmq')
    Invoke-EventCompose @('up', '--detach', '--wait', '--wait-timeout', '120', '--no-deps', '--force-recreate', 'order-service')
    $confirmedKey = 'confirmed-' + [Guid]::NewGuid().ToString('N')
    $rejectedKey = 'rejected-' + [Guid]::NewGuid().ToString('N')
    $confirmedBody = @{ items = @(@{ productId = $product.id; quantity = 2 }) }
    $rejectedBody = @{ items = @(@{ productId = $product.id; quantity = 20 }) }
    $confirmed = Send-EventJson 'POST' 'http://localhost:5103/api/orders' $confirmedBody 201 $confirmedKey
    $rejected = Send-EventJson 'POST' 'http://localhost:5103/api/orders' $rejectedBody 409 $rejectedKey
    if ($confirmed.status -ne 'Confirmed' -or $rejected.status -ne 'Rejected') { throw 'Broker outage changed order outcomes.' }
    $predicate = "AggregateId IN ('$([Guid]$confirmed.orderId)', '$([Guid]$rejected.orderId)')"
    if ((Read-EventCount "$predicate AND PublishedAtUtc IS NULL") -ne 2) { throw 'Outbox did not retain both unpublished events.' }
    Wait-EventCount "$predicate AND AttemptCount > 0 AND PublishedAtUtc IS NULL AND LastError IS NOT NULL" 2 45
    foreach ($port in 5101, 5102, 5103) {
        if ((Invoke-RestMethod "http://localhost:$port/health") -ne 'Healthy') { throw "API $port is not healthy during broker outage." }
    }
    $null = Send-EventJson 'POST' 'http://localhost:5103/api/orders' $confirmedBody 200 $confirmedKey
    $null = Send-EventJson 'POST' 'http://localhost:5103/api/orders' $rejectedBody 409 $rejectedKey
    if ((Read-EventCount $predicate) -ne 2) { throw 'Terminal replay created another logical event.' }
    Write-Output 'Broker down: OrderService restarts healthy; Confirmed=201, Rejected=409; two durable unpublished events; replays create no extra event.'

    Invoke-EventCompose @('up', '--detach', '--wait', '--wait-timeout', '180', 'rabbitmq')
    Wait-EventCount "$predicate AND PublishedAtUtc IS NOT NULL" 2
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    $consumed = $false
    do {
        $logs = docker compose --project-directory $projectRoot --file "$projectRoot\docker-compose.yml" logs --no-color --since $started order-event-worker 2>&1 | Out-String
        $consumed = $logs.Contains($confirmed.orderId) -and $logs.Contains($rejected.orderId)
        if (-not $consumed) { Start-Sleep -Seconds 2 }
    } while (-not $consumed -and [DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $consumed) { throw 'Worker did not log both recovered order notifications.' }
    $balance = Send-EventJson 'GET' $inventory $null
    if ($balance.onHand -ne 10 -or $balance.reserved -ne 3 -or $balance.availableQuantity -ne 7) {
        throw 'Notification delivery changed inventory business state.'
    }
    Write-Output 'Broker recovered: both events confirmed/published and consumed; OnHand/Reserved/Available remain 10/3/7.'
    Write-Output "Confirmed order: $($confirmed.orderId); Rejected order: $($rejected.orderId). Demo data retained."
}
finally {
    try { Invoke-EventCompose @('up', '--detach', '--wait', '--wait-timeout', '180', 'rabbitmq', 'order-event-worker', 'order-service') }
    finally { $client.Dispose() }
}
