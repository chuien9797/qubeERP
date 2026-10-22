param([int]$ProductPort = 5101, [int]$InventoryPort = 5102, [int]$OrderPort = 5103)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$composeFile = Join-Path $projectRoot 'docker-compose.yml'

function Send-Json {
    param([string]$Method, [string]$Url, $Body, [int]$Expected = 200, [string]$Key)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Url)
    if ($null -ne $Body) {
        $request.Content = [System.Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 8), [Text.Encoding]::UTF8, 'application/json')
    }
    if ($Key) { $request.Headers.Add('Idempotency-Key', $Key) }
    try {
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne $Expected) { throw "Expected $Expected; received $($response.StatusCode): $text" }
            return ($text | ConvertFrom-Json)
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

$brokerStopped = $false
try {
    $orderBase = "http://localhost:$OrderPort"
    $inventoryBase = "http://localhost:$InventoryPort"
    $schema = Send-Json GET "$orderBase/swagger/v1/swagger.json" $null
    foreach ($path in '/api/orders/{id}/cancel', '/api/orders/{id}/reconcile-cancellation', '/api/orders') {
        if (-not $schema.paths.PSObject.Properties[$path]) { throw "Missing Swagger path $path" }
    }
    foreach ($code in '200', '202', '400', '404', '409') {
        if (-not $schema.paths.'/api/orders/{id}/cancel'.post.responses.PSObject.Properties[$code]) { throw "Missing cancellation response $code" }
    }
    if ($schema.components.schemas.CancelOrderRequest.properties.reason.maxLength -ne 500) { throw 'Missing reason maximum in Swagger' }
    $inventorySchema = Send-Json GET "$inventoryBase/swagger/v1/swagger.json" $null
    if (-not $inventorySchema.paths.PSObject.Properties['/api/inventory/reservations/{reservationId}/release']) { throw 'Missing release Swagger endpoint' }
    Write-Output 'Swagger: cancel/reconcile/list/release paths, response codes and reason schema verified.'

    $products = @(foreach ($quantity in 3, 2) {
        $product = Send-Json POST "http://localhost:$ProductPort/api/products" @{
            sku = 'cancel-' + [Guid]::NewGuid().ToString('N'); name = 'Cancellation verification'; unitPrice = 12.50
        } 201
        $null = Send-Json PUT "$inventoryBase/api/inventory/$($product.id)" @{ onHand = 20; reason = 'Cancellation verification opening count' }
        @{ productId = $product.id; quantity = $quantity }
    })
    $key = [Guid]::NewGuid().ToString('N')
    $body = @{ items = $products }
    $order = Send-Json POST "$orderBase/api/orders" $body 201 $key
    if ($order.status -ne 'Confirmed') { throw 'Existing creation did not confirm' }
    foreach ($line in $products) {
        $stock = Send-Json GET "$inventoryBase/api/inventory/$($line.productId)" $null
        if ($stock.onHand -ne 20 -or $stock.reserved -ne $line.quantity) { throw 'Incorrect reservation balances' }
    }
    Write-Output "Creation unchanged: HTTP 201 Confirmed, order $($order.orderId); holds 3 and 2, OnHand 20 each."

    docker compose --project-directory $projectRoot -f $composeFile stop rabbitmq
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop project broker for outage verification' }
    $brokerStopped = $true
    $cancelled = Send-Json POST "$orderBase/api/orders/$($order.orderId)/cancel" @{ reason = 'Customer cancelled before shipment' }
    if ($cancelled.status -ne 'Cancelled') { throw 'Cancellation failed during broker outage' }
    $replayed = Send-Json POST "$orderBase/api/orders/$($order.orderId)/cancel" @{ reason = 'Ignored replay reason' }
    if ($replayed.cancellationReason -ne 'Customer cancelled before shipment') { throw 'Cancellation reason changed on replay' }
    $reconciled = Send-Json POST "$orderBase/api/orders/$($order.orderId)/reconcile-cancellation" $null
    if ($reconciled.status -ne 'Cancelled') { throw 'Cancelled reconcile replay failed' }
    $createReplay = Send-Json POST "$orderBase/api/orders" $body 200 $key
    if ($createReplay.status -ne 'Cancelled') { throw 'Original key lost its cancelled outcome' }
    foreach ($line in $products) {
        $stock = Send-Json GET "$inventoryBase/api/inventory/$($line.productId)" $null
        if ($stock.onHand -ne 20 -or $stock.reserved -ne 0 -or $stock.availableQuantity -ne 20) { throw 'Incorrect released balances' }
    }
    Write-Output 'Cancellation and replays: HTTP 200 Cancelled while RabbitMQ stopped; first reason preserved; balances 20/0/20 each.'

    $firstId = $products[0].productId; $secondId = $products[1].productId; $orderId = $order.orderId
    $query = @"
SET NOCOUNT ON;
USE InventoryDb;
IF (SELECT COUNT(*) FROM StockItems WHERE ProductId IN ('$firstId','$secondId') AND OnHand=20 AND Reserved=0) <> 2 THROW 50000, 'Stock balances incorrect', 1;
IF (SELECT COUNT(*) FROM StockMovements WHERE OperationId='$orderId' AND MovementType='Release' AND OnHandDelta=0 AND ReservedDelta<0 AND OnHandAfter=20 AND ReservedAfter=0 AND Reason=N'Customer cancelled before shipment') <> 2 THROW 50000, 'Release ledger incorrect', 1;
IF (SELECT SUM(ReservedDelta) FROM StockMovements WHERE OperationId='$orderId' AND MovementType='Release') <> -5 THROW 50000, 'Release quantity incorrect', 1;
IF EXISTS (SELECT s.ProductId FROM StockItems s JOIN StockMovements m ON m.ProductId=s.ProductId WHERE s.ProductId IN ('$firstId','$secondId') GROUP BY s.ProductId,s.OnHand,s.Reserved HAVING SUM(m.OnHandDelta)<>s.OnHand OR SUM(m.ReservedDelta)<>s.Reserved) THROW 50000, 'Ledger reconciliation failed', 1;
SELECT ProductId,OnHand,Reserved,OnHand-Reserved AS Available FROM StockItems WHERE ProductId IN ('$firstId','$secondId');
SELECT OperationId,ProductId,MovementType,OnHandDelta,ReservedDelta,OnHandAfter,ReservedAfter,Reason FROM StockMovements WHERE OperationId='$orderId' ORDER BY ProductId,Id;
USE OrderDb;
IF (SELECT COUNT(*) FROM OutboxMessages WHERE AggregateId='$orderId' AND EventType='OrderCancelled' AND EventVersion=1 AND PublishedAtUtc IS NULL) <> 1 THROW 50000, 'Cancellation outbox missing during broker outage', 1;
IF NOT EXISTS (SELECT 1 FROM Orders WHERE Id='$orderId' AND Status='Cancelled' AND CancellationReason=N'Customer cancelled before shipment') THROW 50000, 'Order completion missing', 1;
SELECT AggregateId,EventType,EventVersion,PublishedAtUtc FROM OutboxMessages WHERE AggregateId='$orderId';
"@
    $query | docker compose --project-directory $projectRoot -f $composeFile exec -T sqlserver bash -c 'SQLCMDPASSWORD=$MSSQL_SA_PASSWORD /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b'
    if ($LASTEXITCODE -ne 0) { throw 'Direct SQL verification failed' }
    Write-Output "Direct SQL verified balances, ledger deltas, release reason and one pending cancellation outbox event. OrderId=$orderId"
}
finally {
    if ($brokerStopped) {
        docker compose --project-directory $projectRoot -f $composeFile start rabbitmq
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Restore this project broker manually: docker compose start rabbitmq' }
    }
    $client.Dispose()
}
