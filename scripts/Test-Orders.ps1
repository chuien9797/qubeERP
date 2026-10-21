$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$orders = 'http://localhost:5103/api/orders'
$inventory = 'http://localhost:5102/api/inventory'

function New-JsonRequest {
    param([string]$Method, [string]$Url, $Body, [string]$Key)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Url)
    if ($null -ne $Body) {
        $request.Content = [System.Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 8), [Text.Encoding]::UTF8, 'application/json')
    }
    if ($Key) { $request.Headers.Add('Idempotency-Key', $Key) }
    return $request
}

function Send-Json {
    param([string]$Method, [string]$Url, $Body, [int]$ExpectedStatus = 200, [string]$Key)
    $request = New-JsonRequest $Method $Url $Body $Key
    try {
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne $ExpectedStatus) {
                throw "Expected $ExpectedStatus for $Method $Url; received $($response.StatusCode): $json"
            }
            return ($json | ConvertFrom-Json)
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function New-DemoProduct {
    param([decimal]$Price, [int]$OnHand)
    $product = Send-Json 'POST' 'http://localhost:5101/api/products' @{
        sku = 'order-' + [Guid]::NewGuid().ToString('N'); name = 'Order demo'; unitPrice = $Price
    } 201
    $null = Send-Json 'PUT' "$inventory/$($product.id)" @{ onHand = $OnHand; reason = 'Order demo setup' }
    return $product.id
}

try {
    $schema = Send-Json 'GET' 'http://localhost:5103/swagger/v1/swagger.json' $null
    $keyParameter = @($schema.paths.'/api/orders'.post.parameters | Where-Object { $_.name -eq 'Idempotency-Key' -and $_.in -eq 'header' })
    if ($keyParameter.Count -ne 1 -or -not $keyParameter[0].required) { throw 'Swagger does not describe the required idempotency header.' }
    if (-not $schema.components.schemas.CreateOrderRequest.example) { throw 'Order request example is missing.' }
    Write-Output 'Order Swagger exposes POST/GET, required Idempotency-Key and request example'

    $first = New-DemoProduct 12.50 10
    $second = New-DemoProduct 7.25 1
    $body = @{ items = @(@{ productId = $first; quantity = 3 }, @{ productId = $second; quantity = 2 }) }
    $rejectedKey = 'shortage-' + [Guid]::NewGuid().ToString('N')
    $rejected = Send-Json 'POST' $orders $body 409 $rejectedKey
    if ($rejected.errorCode -ne 'INSUFFICIENT_STOCK' -or $rejected.status -ne 'Rejected') { throw 'Order did not persist its rejection.' }
    foreach ($product in @($first, $second)) {
        $stock = Send-Json 'GET' "$inventory/$product" $null
        $ledger = @(Send-Json 'GET' "$inventory/$product/movements" $null)
        if ($stock.reserved -ne 0 -or @($ledger | Where-Object { $_.operationId -eq $rejected.orderId }).Count -ne 0) {
            throw 'Rejected order partially held stock.'
        }
    }
    $null = Send-Json 'PUT' "$inventory/$second" @{ onHand = 5; reason = 'Restock order demo' }
    $replay = Send-Json 'POST' $orders $body 409 $rejectedKey
    if ($replay.orderId -ne $rejected.orderId -or $replay.shortfalls[0].available -ne 1) { throw 'Rejected replay changed after restock.' }
    Write-Output 'Two-item shortage=409 Rejected; zero holds/Reserve movements; replay after restock preserves original shortage'

    $key = 'confirmed-' + [Guid]::NewGuid().ToString('N')
    $confirmed = Send-Json 'POST' $orders $body 201 $key
    if ($confirmed.status -ne 'Confirmed' -or $confirmed.totalAmount -ne 52 -or $confirmed.lines.Count -ne 2) { throw 'Incorrect confirmed snapshots or totals.' }
    $body.items = @($body.items[1], $body.items[0])
    $replay = Send-Json 'POST' $orders $body 200 $key
    $read = Send-Json 'GET' "$orders/$($confirmed.orderId)" $null
    if ($replay.orderId -ne $confirmed.orderId -or $read.totalAmount -ne 52) { throw 'Confirmed replay did not preserve the original order.' }
    $firstStock = Send-Json 'GET' "$inventory/$first" $null
    $secondStock = Send-Json 'GET' "$inventory/$second" $null
    if ($firstStock.onHand -ne 10 -or $firstStock.reserved -ne 3 -or $firstStock.availableQuantity -ne 7 -or
        $secondStock.onHand -ne 5 -or $secondStock.reserved -ne 2 -or $secondStock.availableQuantity -ne 3) {
        throw 'Confirmed order did not reserve the exact stock quantities.'
    }
    foreach ($product in @($first, $second)) {
        $ledger = @(Send-Json 'GET' "$inventory/$product/movements" $null)
        $rows = @($ledger | Where-Object { $_.operationId -eq $confirmed.orderId -and $_.movementType -eq 'Reserve' })
        if ($rows.Count -ne 1 -or $rows[0].onHandDelta -ne 0) { throw 'Replay duplicated a hold or changed physical stock.' }
    }
    Write-Output 'Order=201 Confirmed; total=52; reordered replay=200; GET preserves snapshots; physical stock unchanged; one Reserve row per line'

    $conflict = Send-Json 'POST' $orders @{ items = @(@{ productId = $first; quantity = 4 }) } 409 $key
    if ($conflict.errorCode -ne 'IDEMPOTENCY_KEY_CONFLICT') { throw 'Changed order payload did not conflict.' }

    $raceKey = 'race-' + [Guid]::NewGuid().ToString('N')
    $raceBody = @{ items = @(@{ productId = $first; quantity = 1 }, @{ productId = $second; quantity = 1 }) }
    $requests = @((New-JsonRequest 'POST' $orders $raceBody $raceKey), (New-JsonRequest 'POST' $orders $raceBody $raceKey))
    $responses = @()
    try {
        $tasks = @($client.SendAsync($requests[0]), $client.SendAsync($requests[1]))
        $responses = @($tasks[0].GetAwaiter().GetResult(), $tasks[1].GetAwaiter().GetResult())
        $results = @()
        $createdCount = 0
        foreach ($response in $responses) {
            $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -notin @(200, 201, 202)) { throw "Concurrent order failed: $json" }
            if ([int]$response.StatusCode -eq 201) { $createdCount++ }
            $results += ($json | ConvertFrom-Json)
        }
        if ($createdCount -ne 1 -or $results[0].orderId -ne $results[1].orderId) { throw 'Concurrent requests created different orders.' }
        $raceId = $results[0].orderId
    }
    finally {
        foreach ($response in $responses) { $response.Dispose() }
        foreach ($request in $requests) { $request.Dispose() }
    }
    $raceOrder = Send-Json 'GET' "$orders/$raceId" $null
    if ($raceOrder.status -ne 'Confirmed') { throw 'Winning concurrent order did not confirm.' }
    foreach ($product in @($first, $second)) {
        $ledger = @(Send-Json 'GET' "$inventory/$product/movements" $null)
        $rows = @($ledger | Where-Object { $_.operationId -eq $raceId -and $_.movementType -eq 'Reserve' })
        if ($rows.Count -ne 1 -or $rows[0].reservedDelta -ne 1) { throw 'Concurrent same-key requests duplicated stock effects.' }
    }
    Write-Output 'Concurrent same-key POSTs share one order ID and exactly one Reserve movement per product'

    $oversizedKey = 'oversized-' + [Guid]::NewGuid().ToString('N')
    $oversized = @{ items = @(@{ productId = $first; quantity = 1 }); padding = 'x' * 70000 }
    $problem = Send-Json 'POST' $orders $oversized 413 $oversizedKey
    if ($problem.errorCode -ne 'REQUEST_TOO_LARGE') { throw 'Incorrect oversized request response.' }
    $null = Send-Json 'POST' $orders @{ items = @(@{ productId = $first; quantity = 1 }) } 201 $oversizedKey
    $null = Send-Json 'GET' "$orders/$([Guid]::NewGuid())" $null 404
    Write-Output 'Changed payload=409; oversized request=413 without claiming key; valid retry=201; missing order=404'
}
finally { $client.Dispose() }
