$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()

function Send-JsonRequest {
    param([string]$Method, [string]$Url, $Body, [int]$ExpectedStatus = 200)

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Url)
    try {
        if ($null -ne $Body) {
            $json = $Body | ConvertTo-Json
            $request.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
        }

        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne $ExpectedStatus) {
                throw "Expected HTTP $ExpectedStatus for $Method $Url; received $($response.StatusCode): $json"
            }

            return ($json | ConvertFrom-Json)
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

try {
    $product = Send-JsonRequest 'POST' 'http://localhost:5101/api/products' @{
        sku = 'inventory-' + [Guid]::NewGuid().ToString('N')
        name = 'Inventory smoke product'
        unitPrice = 19.90
    } 201
    $url = "http://localhost:5102/api/inventory/$($product.id)"
    $balance = Send-JsonRequest 'PUT' $url @{ onHand = 10; reason = 'Initial stocktake' }
    if ($balance.onHand -ne 10 -or $balance.reserved -ne 0 -or $balance.availableQuantity -ne 10) {
        throw 'Expected initial stock 10/0/10.'
    }

    $read = Send-JsonRequest 'GET' $url $null
    if ($read.productId -ne $product.id -or $read.onHand -ne 10) { throw 'Stock GET returned an incorrect balance.' }
    $movements = @(Send-JsonRequest 'GET' "$url/movements" $null)
    if ($movements.Count -ne 1 -or $movements[0].movementType -ne 'InitialStock' -or $movements[0].onHandDelta -ne 10) {
        throw 'Initial stock movement is incorrect.'
    }
    Write-Output 'Initial PUT and GET=200; OnHand/Reserved/Available=10/0/10; one InitialStock movement'

    $null = Send-JsonRequest 'PUT' $url @{ onHand = 10; reason = 'Repeated stocktake' }
    $movements = @(Send-JsonRequest 'GET' "$url/movements" $null)
    if ($movements.Count -ne 1) { throw 'No-op stock PUT added a ledger row.' }
    Write-Output 'Repeated PUT=200; no additional movement'

    $balance = Send-JsonRequest 'PUT' $url @{ onHand = 7; reason = 'Corrected stocktake' }
    $movements = @(Send-JsonRequest 'GET' "$url/movements" $null)
    if ($balance.availableQuantity -ne 7 -or $balance.reserved -ne 0 -or $movements.Count -ne 2 -or $movements[1].onHandDelta -ne -3 -or $movements[1].movementType -ne 'StockSet') {
        throw 'Changed stock PUT or its ledger delta is incorrect.'
    }
    Write-Output 'Changed PUT=200; stock=7/0/7; StockSet delta=-3'

    $missingUrl = 'http://localhost:5102/api/inventory/' + [Guid]::NewGuid().ToString()
    $problem = Send-JsonRequest 'PUT' $missingUrl @{ onHand = 10; reason = 'Unknown product' } 422
    if ($problem.errorCode -ne 'PRODUCT_NOT_FOUND') { throw 'Missing Product did not return PRODUCT_NOT_FOUND.' }
    $null = Send-JsonRequest 'GET' $missingUrl $null 404
    Write-Output 'Missing Product PUT=422 PRODUCT_NOT_FOUND; inventory GET=404'

    $schema = Send-JsonRequest 'GET' 'http://localhost:5102/swagger/v1/swagger.json' $null
    if (-not $schema.paths.'/api/inventory/{productId}'.put -or -not $schema.paths.'/api/inventory/{productId}/movements'.get -or $schema.components.schemas.SetStockRequest.example.onHand -ne 10) {
        throw 'Inventory Swagger operations or example missing.'
    }
    Write-Output 'Inventory Swagger routes and stocktake example verified'
}
finally {
    $client.Dispose()
}
