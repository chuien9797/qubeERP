$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$inventory = 'http://localhost:5102/api/inventory'

function Send-Json {
    param([string]$Method, [string]$Url, $Body, [int]$ExpectedStatus = 200)

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Url)
    try {
        if ($null -ne $Body) {
            $request.Content = [System.Net.Http.StringContent]::new(
                ($Body | ConvertTo-Json -Depth 6), [Text.Encoding]::UTF8, 'application/json')
        }

        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne $ExpectedStatus) {
                throw "Expected HTTP $ExpectedStatus for $Method $Url; received $($response.StatusCode): $json"
            }

            return ($json | ConvertFrom-Json)
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function New-DemoStock {
    param([int]$Quantity)

    $product = Send-Json 'POST' 'http://localhost:5101/api/products' @{
        sku = 'reservation-' + [Guid]::NewGuid().ToString('N')
        name = 'Reservation smoke product'
        unitPrice = 10
    } 201
    $null = Send-Json 'PUT' "$inventory/$($product.id)" @{ onHand = $Quantity; reason = 'Reservation demo setup' }
    return $product.id
}

try {
    $first = New-DemoStock 10
    $second = New-DemoStock 1
    $rejectedId = [Guid]::NewGuid().ToString()
    $rejectedRequest = @{
        reservationId = $rejectedId
        items = @(@{ productId = $first; quantity = 2 }, @{ productId = $second; quantity = 2 })
    }
    $failure = Send-Json 'POST' "$inventory/reservations" $rejectedRequest 409
    if ($failure.errorCode -ne 'INSUFFICIENT_STOCK' -or $failure.reservationId -ne $rejectedId -or $failure.shortfalls[0].available -ne 1) {
        throw 'Shortage did not return the original shortfall and reservation ID.'
    }
    foreach ($id in @($first, $second)) {
        $balance = Send-Json 'GET' "$inventory/$id" $null
        $ledger = @(Send-Json 'GET' "$inventory/$id/movements" $null)
        if ($balance.reserved -ne 0 -or $ledger.Count -ne 1) { throw 'Rejected reservation changed stock or ledger.' }
    }
    Write-Output 'Two-item shortage=409; neither product reserved; no Reserve ledger rows'

    $null = Send-Json 'PUT' "$inventory/$second" @{ onHand = 5; reason = 'Replenishment after rejection' }
    $failure = Send-Json 'POST' "$inventory/reservations" $rejectedRequest 409
    $rejected = Send-Json 'GET' "$inventory/reservations/$rejectedId" $null
    if ($failure.errorCode -ne 'INSUFFICIENT_STOCK' -or $failure.shortfalls[0].available -ne 1 -or $rejected.status -ne 'Rejected' -or $rejected.shortfalls[0].available -ne 1) {
        throw 'Rejected replay changed its original outcome after restock.'
    }
    Write-Output 'Rejected replay after restock=409; persisted original available=1; GET status=Rejected'

    $id = [Guid]::NewGuid().ToString()
    $request = @{
        reservationId = $id
        items = @(@{ productId = $first; quantity = 3 }, @{ productId = $second; quantity = 2 })
    }
    $reserved = Send-Json 'POST' "$inventory/reservations" $request 201
    if ($reserved.status -ne 'Reserved' -or $reserved.reservationId -ne $id) { throw 'Reservation was not confirmed.' }
    $request.items = @($request.items[1], $request.items[0])
    $replay = Send-Json 'POST' "$inventory/reservations" $request
    $queried = Send-Json 'GET' "$inventory/reservations/$id" $null
    if ($replay.reservationId -ne $id -or $queried.status -ne 'Reserved') { throw 'Successful replay or GET returned an incorrect outcome.' }
    $firstBalance = Send-Json 'GET' "$inventory/$first" $null
    $secondBalance = Send-Json 'GET' "$inventory/$second" $null
    if ($firstBalance.onHand -ne 10 -or $firstBalance.reserved -ne 3 -or $firstBalance.availableQuantity -ne 7 -or $secondBalance.onHand -ne 5 -or $secondBalance.reserved -ne 2) {
        throw 'Physical or reserved stock is incorrect after replay.'
    }
    foreach ($product in @($first, $second)) {
        $ledger = @(Send-Json 'GET' "$inventory/$product/movements" $null)
        $reserveRows = @($ledger | Where-Object { $_.operationId -eq $id -and $_.movementType -eq 'Reserve' })
        if ($reserveRows.Count -ne 1 -or $reserveRows[0].onHandDelta -ne 0) { throw 'Reservation ledger duplicated or changed physical stock.' }
    }
    Write-Output 'Reservation=201; reordered replay=200; GET=Reserved; one Reserve row per product; OnHand unchanged'

    $request.items = @(@{ productId = $first; quantity = 4 }, @{ productId = $second; quantity = 2 })
    $conflict = Send-Json 'POST' "$inventory/reservations" $request 409
    if ($conflict.errorCode -ne 'RESERVATION_PAYLOAD_CONFLICT') { throw 'Changed payload did not conflict.' }
    $null = Send-Json 'GET' "$inventory/reservations/$([Guid]::NewGuid())" $null 404
    Write-Output 'Changed payload=409 RESERVATION_PAYLOAD_CONFLICT; absent reservation GET=404'

    $oversizedId = [Guid]::NewGuid().ToString()
    $oversized = @{
        reservationId = $oversizedId
        items = @(@{ productId = $first; quantity = 1 })
        padding = 'x' * 70000
    }
    $tooLarge = Send-Json 'POST' "$inventory/reservations" $oversized 413
    if ($tooLarge.errorCode -ne 'REQUEST_TOO_LARGE') { throw 'Oversized request did not retain its 413 error code.' }
    $null = Send-Json 'GET' "$inventory/reservations/$oversizedId" $null 404
    Write-Output 'Oversized request=413 REQUEST_TOO_LARGE; no reservation persisted'
}
finally { $client.Dispose() }
