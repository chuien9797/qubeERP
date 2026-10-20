param([string]$Sku = ('mouse-01-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.BaseAddress = [Uri]'http://localhost:5101'
try {
    $body = @{ sku = " $Sku "; name = ' Wireless Mouse '; description = '2.4 GHz'; unitPrice = 59.90 } | ConvertTo-Json
    $content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
    $created = $client.PostAsync('/api/products', $content).GetAwaiter().GetResult()
    try {
        if ([int]$created.StatusCode -ne 201) { throw "Expected 201 Created; got $($created.StatusCode). Use a new -Sku if already created." }
        $product = $created.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if ($product.sku -cne $Sku.Trim().ToUpperInvariant() -or $product.name -cne 'Wireless Mouse' -or $product.unitPrice -ne 59.90 -or -not $product.isActive) {
            throw 'Created product snapshot is incorrect.'
        }
        $retrieved = $client.GetStringAsync($created.Headers.Location).GetAwaiter().GetResult() | ConvertFrom-Json
        if ($retrieved.id -ne $product.id) { throw 'Created product cannot be retrieved from Location.' }
        Write-Output "Created=201; sku=$($product.sku); unitPrice=$($product.unitPrice); Location GET=200"
    }
    finally {
        $created.Dispose()
        $content.Dispose()
    }

    $content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
    $duplicate = $client.PostAsync('/api/products', $content).GetAwaiter().GetResult()
    try {
        $problem = $duplicate.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if ([int]$duplicate.StatusCode -ne 409 -or $problem.errorCode -ne 'DUPLICATE_SKU') { throw 'Duplicate SKU did not return 409 DUPLICATE_SKU.' }
        Write-Output 'Duplicate=409 DUPLICATE_SKU'
    }
    finally {
        $duplicate.Dispose()
        $content.Dispose()
    }

    $missingId = [Guid]::NewGuid().ToString()
    $body = @{ productIds = @($product.id, $missingId) } | ConvertTo-Json
    $content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
    $response = $client.PostAsync('/api/products/lookup', $content).GetAwaiter().GetResult()
    try {
        $lookup = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if ([int]$response.StatusCode -ne 200 -or $lookup.products.Count -ne 1 -or $lookup.products[0].id -ne $product.id -or $lookup.products[0].unitPrice -ne 59.90 -or -not $lookup.products[0].isActive -or $lookup.missingProductIds[0] -ne $missingId) {
            throw 'Lookup did not return the expected snapshot and missing ID.'
        }
        Write-Output 'Lookup=200; authoritative price/active status and missing ID verified'
    }
    finally {
        $response.Dispose()
        $content.Dispose()
    }

    $schema = $client.GetStringAsync('/swagger/v1/swagger.json').GetAwaiter().GetResult() | ConvertFrom-Json
    if (-not $schema.paths.'/api/products'.post -or -not $schema.paths.'/api/products/lookup'.post -or -not $schema.paths.'/api/products/{id}'.get) { throw 'Product operations missing from Swagger.' }
    if ($schema.components.schemas.CreateProductRequest.example.sku -cne ' mouse-01 ') { throw 'Product Swagger example missing.' }
    Write-Output 'Swagger product routes and request examples verified'
}
finally {
    $client.Dispose()
}
