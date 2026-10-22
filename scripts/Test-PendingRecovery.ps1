$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$orders = 'http://localhost:5103/api/orders'
$inventory = 'http://localhost:5102/api/inventory'
$projectRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Compose {
    param([string[]]$ComposeArguments)
    & docker compose --project-directory $projectRoot --file "$projectRoot\docker-compose.yml" @ComposeArguments
    if ($LASTEXITCODE -ne 0) { throw "Compose failed: $ComposeArguments" }
}

function Send-Json {
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

function New-DemoProduct {
    param([decimal]$Price, [int]$OnHand)
    $product = Send-Json 'POST' 'http://localhost:5101/api/products' @{
        sku = 'recovery-' + [Guid]::NewGuid().ToString('N'); name = 'Recovery demo'; unitPrice = $Price
    } 201
    $null = Send-Json 'PUT' "$inventory/$($product.id)" @{ onHand = $OnHand; reason = 'Recovery demo setup' }
    return $product.id
}

try {
    $first = New-DemoProduct 12.50 10
    $second = New-DemoProduct 7.25 5
    $missingBody = @{ items = @(@{ productId = $first; quantity = 3 }, @{ productId = $second; quantity = 2 }) }
    $committedBody = @{ items = @(@{ productId = $first; quantity = 1 }, @{ productId = $second; quantity = 1 }) }
    $rejectedBody = @{ items = @(@{ productId = $first; quantity = 9 }, @{ productId = $second; quantity = 1 }) }
    $missingKey = 'missing-' + [Guid]::NewGuid().ToString('N')
    $committedKey = 'committed-' + [Guid]::NewGuid().ToString('N')
    $rejectedKey = 'rejected-' + [Guid]::NewGuid().ToString('N')

    Invoke-Compose -ComposeArguments @('stop', 'inventory-service')
    $missing = Send-Json 'POST' $orders $missingBody 202 $missingKey
    $committed = Send-Json 'POST' $orders $committedBody 202 $committedKey
    $rejected = Send-Json 'POST' $orders $rejectedBody 202 $rejectedKey
    $uncertain = Send-Json 'POST' $orders $missingBody 202 $missingKey
    if ($uncertain.orderId -ne $missing.orderId -or $uncertain.status -ne 'Pending') { throw 'Uncertain query changed the Pending operation.' }
    $pendingRead = Send-Json 'GET' "$orders/$($missing.orderId)" $null
    if ($pendingRead.status -ne 'Pending' -or $pendingRead.totalAmount -ne 52) { throw 'Pending GET did not retain snapshots.' }
    Write-Output 'Inventory down: new orders and same-key uncertain query return 202; GET is read-only and retains saved prices'

    Invoke-Compose -ComposeArguments @('stop', 'product-service')
    Invoke-Compose -ComposeArguments @('up', '--detach', '--wait', '--wait-timeout', '120', 'inventory-service')
    $recovered = Send-Json 'POST' $orders $missingBody 200 $missingKey
    if ($recovered.orderId -ne $missing.orderId -or $recovered.status -ne 'Confirmed' -or $recovered.totalAmount -ne 52) {
        throw 'Missing reservation did not recover using its saved ID/prices.'
    }
    Write-Output 'Product down: absent reservation recovered with same order ID and original total 52'

    $null = Send-Json 'POST' "$inventory/reservations" @{ reservationId = $committed.orderId; items = $committedBody.items } 201
    $stillPending = Send-Json 'GET' "$orders/$($committed.orderId)" $null
    if ($stillPending.status -ne 'Pending') { throw 'Order GET unexpectedly reconciled Inventory.' }
    $recovered = Send-Json 'POST' $orders $committedBody 200 $committedKey
    if ($recovered.orderId -ne $committed.orderId -or $recovered.status -ne 'Confirmed') { throw 'Existing Reserved outcome did not confirm.' }
    Write-Output 'Durable Reserved outcome with Pending order: GET stays Pending; same-key POST confirms without another hold'

    $shortage = Send-Json 'POST' "$inventory/reservations" @{ reservationId = $rejected.orderId; items = $rejectedBody.items } 409
    if ($shortage.errorCode -ne 'INSUFFICIENT_STOCK' -or $shortage.shortfalls[0].available -ne 6) { throw 'Unexpected durable shortage.' }
    $null = Send-Json 'PUT' "$inventory/$first" @{ onHand = 20; reason = 'Restock after durable rejection' }
    $recovered = Send-Json 'POST' $orders $rejectedBody 409 $rejectedKey
    if ($recovered.orderId -ne $rejected.orderId -or $recovered.orderStatus -ne 'Rejected' -or $recovered.shortfalls[0].available -ne 6) {
        throw 'Recovery did not retain the original rejection after restock.'
    }
    Write-Output 'Durable Rejected outcome after restock: same-key POST returns original 409 and available=6'

    $null = Send-Json 'POST' $orders $missingBody 200 $missingKey
    $null = Send-Json 'POST' $orders $rejectedBody 409 $rejectedKey
    foreach ($product in @($first, $second)) {
        $ledger = @(Send-Json 'GET' "$inventory/$product/movements" $null)
        foreach ($orderId in @($missing.orderId, $committed.orderId)) {
            $rows = @($ledger | Where-Object { $_.operationId -eq $orderId -and $_.movementType -eq 'Reserve' })
            if ($rows.Count -ne 1 -or $rows[0].onHandDelta -ne 0) { throw 'Recovery duplicated or changed physical stock.' }
        }
        if (@($ledger | Where-Object { $_.operationId -eq $rejected.orderId }).Count -ne 0) { throw 'Rejected recovery wrote a movement.' }
    }
    $firstBalance = Send-Json 'GET' "$inventory/$first" $null
    $secondBalance = Send-Json 'GET' "$inventory/$second" $null
    if ($firstBalance.onHand -ne 20 -or $firstBalance.reserved -ne 4 -or $firstBalance.availableQuantity -ne 16 -or
        $secondBalance.onHand -ne 5 -or $secondBalance.reserved -ne 3 -or $secondBalance.availableQuantity -ne 2) {
        throw 'Recovery balances are incorrect.'
    }
    Write-Output 'No duplicate Reserve rows; no rejected hold; final balances 20/4/16 and 5/3/2; saved prices used while Product stayed down'
}
finally {
    try { Invoke-Compose -ComposeArguments @('up', '--detach', '--wait', '--wait-timeout', '120', 'product-service', 'inventory-service') }
    finally { $client.Dispose() }
}
