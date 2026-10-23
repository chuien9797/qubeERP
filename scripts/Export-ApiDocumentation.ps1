param(
    [ValidateRange(1, 65535)][int]$ProductPort = 5101,
    [ValidateRange(1, 65535)][int]$InventoryPort = 5102,
    [ValidateRange(1, 65535)][int]$OrderPort = 5103
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputFolder = Join-Path $projectRoot 'docs'
$schemaFolder = Join-Path $outputFolder 'openapi'
$utf8 = [Text.UTF8Encoding]::new($false)
$services = @(
    @{ Key = 'products'; Name = 'Product'; Port = $ProductPort; Count = 5 },
    @{ Key = 'inventory'; Name = 'Inventory'; Port = $InventoryPort; Count = 7 },
    @{ Key = 'orders'; Name = 'Order'; Port = $OrderPort; Count = 5 }
)
$methods = @('get', 'post', 'put', 'patch', 'delete', 'head', 'options')

function Html($value) { [Net.WebUtility]::HtmlEncode([string]$value) }
function JsonExample($value) {
    if ($null -eq $value) { return '' }
    return '<pre><code>' + (Html (ConvertTo-Json -InputObject $value -Depth 100)) + '</code></pre>'
}
function SchemaExample($schema, $document) {
    if ($null -eq $schema) { return $null }
    if ($null -ne $schema.example) { return ,$schema.example }
    if ($schema.'$ref') {
        $name = $schema.'$ref'.Split('/')[-1]
        return SchemaExample $document.components.schemas.$name $document
    }
    if ($schema.type -eq 'array') {
        $item = SchemaExample $schema.items $document
        if ($null -ne $item) { return ,@($item) }
    }
    return $null
}
function MediaExamples($media, $document) {
    if ($null -ne $media.example) { return JsonExample $media.example }
    if ($media.examples) {
        return ($media.examples.PSObject.Properties | ForEach-Object {
            '<p><strong>' + (Html $_.Name) + '</strong> ' + (Html $_.Value.summary) + '</p>' + (JsonExample $_.Value.value)
        }) -join "`n"
    }
    return JsonExample (SchemaExample $media.schema $document)
}

# Fetch and validate all three contracts before replacing any handover artifact.
# This exporter makes only read-only requests and never creates demonstration data.
foreach ($service in $services) {
    $url = 'http://127.0.0.1:' + $service.Port + '/swagger/v1/swagger.json'
    $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
    $document = $response.Content | ConvertFrom-Json
    $operations = @($document.paths.PSObject.Properties | ForEach-Object {
        $_.Value.PSObject.Properties | Where-Object { $_.Name -in $methods }
    })
    if (-not $document.openapi -or $operations.Count -ne $service.Count) {
        throw "Unexpected $($service.Name) contract at $url; expected $($service.Count) documented operations. Rebuild the matching API version."
    }
    if ($service.Key -eq 'products' -and -not $document.paths.'/api/products/{productId}/status'.patch.responses.'415') {
        throw 'Product status documentation is stale. Rebuild ProductService before exporting.'
    }
    $service.Raw = $response.Content
    $service.Document = $document
}

$generatedAt = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd HH:mm:ss zzz')
$html = [Text.StringBuilder]::new()
[void]$html.AppendLine(@'
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>QubeErp API reference — examiner handover</title>
<style>
:root{color-scheme:light;--ink:#17253e;--blue:#174abd;--line:#d9e1ee}*{box-sizing:border-box}body{margin:0;overflow-wrap:anywhere;background:#fff;color:var(--ink);font:15px/1.65 system-ui,-apple-system,"Segoe UI",sans-serif}header{padding:38px max(24px,calc((100vw - 1120px)/2));background:#101b30;color:#fff}header p{max-width:850px;color:#dce7ff}h1{font-size:32px;line-height:1.2;margin:0 0 12px}h2{font-size:25px;margin-top:38px}h3{font-size:19px}h4{font-size:15px;margin:18px 0 6px}a{color:var(--blue)}header a{color:#dce7ff}main{max-width:1168px;padding:0 24px 50px;margin:auto}nav{padding:16px 0;border-bottom:1px solid var(--line);display:flex;flex-wrap:wrap;gap:20px}section{scroll-margin-top:20px}article{margin:22px 0;border:1px solid var(--line);border-radius:6px;padding:22px;break-inside:avoid}article h3{margin:0 0 10px;overflow-wrap:anywhere}.method{display:inline-block;background:#eaf0ff;color:#173d92;font:700 12px/1.6 system-ui;padding:3px 9px;margin-right:8px;border-radius:3px}code{font-family:Consolas,monospace;font-size:13px;overflow-wrap:anywhere}pre{background:#f5f7fb;border:1px solid #e1e7f0;padding:14px;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere;font-size:13px;line-height:1.5}table{width:100%;border-collapse:collapse;margin:12px 0;font-size:14px}th,td{text-align:left;vertical-align:top;padding:10px;border-bottom:1px solid var(--line);overflow-wrap:anywhere}th{background:#f5f7fb;color:#203655}.table-wrap{overflow:auto}.notice{border-left:3px solid #2759c5;background:#f3f6fc;padding:12px 16px;margin:20px 0}.meta{color:#52627a}.status{font-weight:700;min-width:60px}details{margin-top:10px}summary{cursor:pointer;font-weight:600}footer{border-top:1px solid var(--line);padding-top:20px;margin-top:32px;color:#52627a}@media(max-width:600px){article{padding:14px}h1{font-size:27px}th,td{padding:7px}}@media print{header{background:#fff;color:#17253e;padding:12px 24px}header p,header a{color:#17253e}nav{display:none}article{break-inside:auto}pre{white-space:pre-wrap}details{display:block}a{color:inherit}body{font-size:11pt}}
</style></head><body><header><h1>QubeErp API reference</h1>
<p>Examiner handover · Product, Inventory and Order services · Local assessment/demo</p>
'@)
[void]$html.AppendLine('<p>Exported from the running APIs: ' + (Html $generatedAt) + ' (Malaysia time). Regenerate with <code>scripts/Export-ApiDocumentation.ps1</code>.</p></header><main>')
[void]$html.AppendLine('<nav aria-label="Document sections"><a href="#workflow">Review workflow</a><a href="#products">Products</a><a href="#inventory">Inventory</a><a href="#orders">Orders</a><a href="reviewer-demo.http">HTTP walkthrough</a><a href="final-review.txt">Review evidence</a></nav>')
[void]$html.AppendLine(@'
<section id="workflow"><h2>How to review</h2><p>Start with README for setup and the sequential HTTP walkthrough for a complete demonstration. This offline reference is generated from the actual OpenAPI metadata. JSON examples illustrate schemas and states; their GUIDs are not real records or captured command results.</p>
<div class="notice"><strong>Local demo boundary.</strong> The APIs have no login/role permissions. Keep them on localhost.</div>
<ol><li>Create two products and initialise stock through Inventory.</li><li>Create a multi-item order with a new Idempotency-Key; inspect balances, reservation items and movements.</li><li>Repeat the original key/body; verify the same order and no second hold. A new shortage request is rejected atomically.</li><li>Add a stock receipt with a stable operationId; replay it without adding twice. Rejected orders retain their original outcome.</li><li>Cancel a Confirmed order; verify Cancelled only after Released, unchanged OnHand and one Release per line.</li><li>Deactivate/reactivate a product; inspect unchanged history. An already validated in-flight/Pending order may still complete.</li></ol>
<h3>Rules that matter to callers</h3><ul><li>Send JSON for body-bearing operations. Bodies are limited to 65,536 bytes. Error responses use application/problem+json; order errors use numeric status and separate orderStatus.</li><li>Use 1–50 distinct products, quantities 1–10,000 per order/reservation line. Keep the original idempotency key/ID and immutable payload on retries. A new identity represents a separate operation.</li><li>Inventory timeout, 408, connection loss or 5xx can follow a committed hold. Repeat the original order POST to recover Pending. GET resources never perform reconciliation.</li><li>Absolute stock counts have no replay identity or automatic uncertain SQL write retry. Inspect stock and movements after a lost response before making a separate count.</li><li>Use Order cancellation for order-owned reservations. Direct Inventory release does not update OrderDb. Keep the first cancellation reason.</li><li>Reservation items and receipt/ledger After balances are historical. GET inventory returns current stock.</li><li>Catalogue isActive is independent of stock. Deactivation is last committed write wins and has no cross-service immediate fence; direct Inventory reservations do not check catalogue activation.</li><li>Amounts use backend decimal arithmetic. JavaScript numeric tooling may lose precision at extreme decimal(18,2) values; this is documented in the final review.</li></ul></section>
'@)

foreach ($service in $services) {
    $document = $service.Document
    [void]$html.AppendLine('<section id="' + $service.Key + '"><h2>' + (Html $document.info.title) + '</h2><p>' + (Html $document.info.description) + '</p>')
    [void]$html.AppendLine('<p><a href="http://localhost:' + $service.Port + '/swagger">Live Swagger</a> · <a href="openapi/' + $service.Key + '.openapi.json">OpenAPI JSON</a></p>')
    [void]$html.AppendLine('<div class="table-wrap"><table><thead><tr><th>Method</th><th>Path</th><th>Purpose</th></tr></thead><tbody>')
    foreach ($path in $document.paths.PSObject.Properties) {
        foreach ($entry in $path.Value.PSObject.Properties | Where-Object { $_.Name -in $methods }) {
            [void]$html.AppendLine('<tr><td>' + (Html $entry.Name.ToUpperInvariant()) + '</td><td><a href="#' + (Html $entry.Value.operationId) + '"><code>' + (Html $path.Name) + '</code></a></td><td>' + (Html $entry.Value.summary) + '</td></tr>')
        }
    }
    [void]$html.AppendLine('</tbody></table></div>')
    foreach ($path in $document.paths.PSObject.Properties) {
        foreach ($entry in $path.Value.PSObject.Properties | Where-Object { $_.Name -in $methods }) {
            $operation = $entry.Value
            [void]$html.AppendLine('<article id="' + (Html $operation.operationId) + '"><h3><span class="method">' + (Html $entry.Name.ToUpperInvariant()) + '</span><code>' + (Html $path.Name) + '</code></h3><p><strong>' + (Html $operation.summary) + '</strong></p><p>' + (Html $operation.description) + '</p>')
            if ($operation.parameters) {
                [void]$html.AppendLine('<h4>Parameters</h4><div class="table-wrap"><table><thead><tr><th>Name / location</th><th>Type / required</th><th>Meaning</th></tr></thead><tbody>')
                foreach ($parameter in $operation.parameters) {
                    $type = [string]$parameter.schema.type
                    if ($parameter.schema.enum) { $type += ': ' + ($parameter.schema.enum -join ', ') }
                    [void]$html.AppendLine('<tr><td><code>' + (Html $parameter.name) + '</code> / ' + (Html $parameter.in) + '</td><td>' + (Html $type) + ' / ' + $(if ($parameter.required) { 'required' } else { 'optional' }) + '</td><td>' + (Html $parameter.description) + '</td></tr>')
                }
                [void]$html.AppendLine('</tbody></table></div>')
            }
            if ($operation.requestBody) {
                [void]$html.AppendLine('<h4>Request body example (illustrative)</h4>')
                $bodyMedia = $operation.requestBody.content.'application/json'
                $bodyType = 'application/json'
                if ($null -eq $bodyMedia) {
                    $firstMedia = @($operation.requestBody.content.PSObject.Properties)[0]
                    $bodyMedia = $firstMedia.Value
                    $bodyType = $firstMedia.Name
                }
                [void]$html.AppendLine('<p><code>' + (Html $bodyType) + '</code></p>' + (MediaExamples $bodyMedia $document))
            }
            [void]$html.AppendLine('<h4>Responses</h4>')
            foreach ($response in $operation.responses.PSObject.Properties) {
                [void]$html.AppendLine('<div><p><span class="status">' + (Html $response.Name) + '</span> ' + (Html $response.Value.description) + '</p>')
                foreach ($media in $response.Value.content.PSObject.Properties) {
                    $example = MediaExamples $media.Value $document
                    if ($example) { [void]$html.AppendLine('<details><summary>Illustrative response · ' + (Html $media.Name) + '</summary>' + $example + '</details>') }
                }
                [void]$html.AppendLine('</div>')
            }
            [void]$html.AppendLine('</article>')
        }
    }
    [void]$html.AppendLine('<h3>Schemas and field descriptions</h3>')
    foreach ($schema in $document.components.schemas.PSObject.Properties) {
        [void]$html.AppendLine('<details><summary>' + (Html $schema.Name) + '</summary><p>' + (Html $schema.Value.description) + '</p>')
        [void]$html.AppendLine('<div class="table-wrap"><table><thead><tr><th>Field</th><th>Schema</th><th>Description</th></tr></thead><tbody>')
        foreach ($property in $schema.Value.properties.PSObject.Properties) {
            $type = [string]$property.Value.type
            if ($property.Value.'$ref') { $type = $property.Value.'$ref'.Split('/')[-1] }
            if ($property.Name -in $schema.Value.required) { $type += ' (required)' }
            [void]$html.AppendLine('<tr><td><code>' + (Html $property.Name) + '</code></td><td>' + (Html $type) + '</td><td>' + (Html $property.Value.description) + '</td></tr>')
        }
        [void]$html.AppendLine('</tbody></table></div></details>')
    }
    [void]$html.AppendLine('</section>')
}
[void]$html.AppendLine('<footer>QubeErp API reference.</footer></main></body></html>')

New-Item -ItemType Directory -Path $schemaFolder -Force | Out-Null
foreach ($service in $services) {
    [IO.File]::WriteAllText((Join-Path $schemaFolder ($service.Key + '.openapi.json')), $service.Raw, $utf8)
}
[IO.File]::WriteAllText((Join-Path $outputFolder 'api-reference.html'), $html.ToString(), $utf8)
Write-Output "Exported 17 operations across 3 OpenAPI documents and docs/api-reference.html. GET requests only; no business records changed."


