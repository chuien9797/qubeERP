param(
    [ValidateRange(1,65535)][int]$ProductPort=5101,
    [ValidateRange(1,65535)][int]$InventoryPort=5102,
    [ValidateRange(1,65535)][int]$OrderPort=5103,
    [switch]$FromFiles
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$docs=Join-Path $root 'docs'
$schemas=Join-Path $docs 'openapi'
$utf8=[Text.UTF8Encoding]::new($false)
$services=@(@{Key='products';Port=$ProductPort;Count=5},@{Key='inventory';Port=$InventoryPort;Count=7},@{Key='orders';Port=$OrderPort;Count=5})
$methods=@('get','post','put','patch','delete','head','options')
$md=[Text.StringBuilder]::new()
function MdCell($v){([string]$v).Replace('|','\|').Replace("`r",'').Replace("`n",' ')}
function Paragraph([string]$v){[void]$md.AppendLine($v+"`n")}
function Heading([int]$level,[string]$v,[string]$anchor){
 if($anchor){[void]$md.AppendLine('<a id="'+$anchor+'"></a>'+"`n")}
 [void]$md.AppendLine(('#'*$level)+' '+$v+"`n")
}
function Table($headers,$rows){
 [void]$md.AppendLine('| '+(($headers|ForEach-Object{MdCell $_})-join ' | ')+' |')
 [void]$md.AppendLine('| '+(($headers|ForEach-Object{'---'})-join ' | ')+' |')
 foreach($row in $rows){
  [void]$md.AppendLine('| '+(($row|ForEach-Object{MdCell $_})-join ' | ')+' |')
 }
 [void]$md.AppendLine()
}
function JsonText($value){
 $json=ConvertTo-Json -InputObject $value -Depth 100
 [regex]::Replace($json,'("[^"\\]*AtUtc"\s*:\s*")([^"\\]+)(")',{
  param($m)
  $date=[DateTimeOffset]::Parse($m.Groups[2].Value,[Globalization.CultureInfo]::InvariantCulture)
  $m.Groups[1].Value+$date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",[Globalization.CultureInfo]::InvariantCulture)+$m.Groups[3].Value
 })
}
function Example($value,[string]$label){
 if($null -eq $value){return}
 Paragraph $label
 $json=JsonText $value
 [void]$md.AppendLine('```json'+"`n"+$json+"`n"+'```'+"`n")
}
function SchemaExample($schema,$document){
 if($null -eq $schema){return $null}
 if($null -ne $schema.example){return ,$schema.example}
 if($schema.'$ref'){return SchemaExample $document.components.schemas.($schema.'$ref'.Split('/')[-1]) $document}
 if($schema.type -eq 'array'){$item=SchemaExample $schema.items $document;if($null -ne $item){return ,@($item)}}
 return $null
}
function MediaExamples($media,$document,[string]$label){
 if($null -ne $media.example){Example $media.example $label;return}
 if($media.examples){foreach($e in $media.examples.PSObject.Properties){Example $e.Value.value ($label+' — '+$e.Name+': '+$e.Value.summary)};return}
 Example (SchemaExample $media.schema $document) $label
}
function SchemaType($schema){
 if($null -eq $schema){return '—'}
 if($schema.'$ref'){return $schema.'$ref'.Split('/')[-1]}
 if($schema.type -eq 'array'){return 'array of '+(SchemaType $schema.items)}
 $type=[string]$schema.type
 foreach($composition in 'allOf','anyOf','oneOf'){if($schema.$composition){$type=($schema.$composition|ForEach-Object{SchemaType $_})-join ' / '}}
 if($schema.format){$type+=' ('+$schema.format+')'}
 if($schema.enum){$type+=': '+($schema.enum -join ', ')}
 if($schema.nullable){$type+='; nullable'}
 if(-not $type){$type='object'}
 return $type
}
foreach($service in $services){
 $raw=if($FromFiles){[IO.File]::ReadAllText((Join-Path $schemas ($service.Key+'.openapi.json')))}else{(Invoke-WebRequest ('http://127.0.0.1:'+$service.Port+'/swagger/v1/swagger.json') -UseBasicParsing -TimeoutSec 30).Content}
 $options=@{}
 if((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')){$options.DateKind='String'}
 $document=ConvertFrom-Json -InputObject $raw @options
 $operations=@($document.paths.PSObject.Properties|ForEach-Object{$_.Value.PSObject.Properties|Where-Object{$_.Name -in $methods}})
 if(-not $document.openapi -or $operations.Count -ne $service.Count){throw "Unexpected $($service.Key) contract; expected $($service.Count) operations."}
 $service.Raw=$raw;$service.Document=$document
}
Heading 1 'QubeErp API reference' ''
Paragraph 'This reference describes the Product, Inventory and Order APIs, including request formats, responses and error handling. It is based on the exported OpenAPI contracts. Examples are illustrative, not captured command results; use IDs and versions returned by your environment.'
[void]$md.AppendLine('See [README](../README.md) for setup and architecture, or the [HTTP walkthrough](reviewer-demo.http) for a complete example workflow.'+"`n")
Heading 2 'Calling conventions' 'conventions'
Paragraph 'Use JSON for command bodies. Preserve the original idempotency key or operation ID and payload on retries. A timed-out write may already have committed. GET endpoints are read-only.'
Paragraph 'For existing physical stock counts, send expectedVersion from GET inventory. A stale or missing version returns 409 STOCK_VERSION_CONFLICT without changing stock or movements. Refresh and review before submitting a new count; omit the version only when initialising absent stock.'
Paragraph 'Recover Pending orders through the original order POST. Use Order cancellation for order-owned reservations. Historical reservation quantities and movement balances are distinct from current inventory.'
Paragraph 'Product activation is independent of stock. Orders already validated before deactivation may still complete. Consumed and Fulfilled are compatibility states; there are no public consumption or fulfilment commands.'
Paragraph 'Errors use application/problem+json. Numeric status is the HTTP status; orderStatus, when present, is the saved order state. UTC timestamp fields use UTC offsets; Angular displays Malaysia time. The local demo has no authentication.'
Heading 2 'Common errors' 'common-errors'
$rows=[Collections.Generic.List[object]]::new()
$rows.Add(@('413','JSON-body endpoints: REQUEST_TOO_LARGE when the request exceeds 65,536 bytes.'))
$rows.Add(@('415','JSON-body endpoints: UNSUPPORTED_MEDIA_TYPE; use Content-Type: application/json.'))
$rows.Add(@('500','Unexpected server/database failure. ProblemDetails exposes an errorCode and traceId, not internal exception details.'))
Table @('Status','Meaning') $rows
foreach($service in $services){
 $document=$service.Document
 Heading 2 $document.info.title $service.Key
 Paragraph $document.info.description
 [void]$md.AppendLine('[Live Swagger](http://localhost:'+$service.Port+'/swagger) · [OpenAPI JSON](openapi/'+$service.Key+'.openapi.json)'+"`n")
 $rows=[Collections.Generic.List[object]]::new()
 foreach($path in $document.paths.PSObject.Properties){foreach($entry in $path.Value.PSObject.Properties|Where-Object{$_.Name -in $methods}){$rows.Add(@($entry.Name.ToUpperInvariant(),$path.Name,$entry.Value.summary))}}
 Table @('Method','Path','Purpose') $rows
 foreach($path in $document.paths.PSObject.Properties){foreach($entry in $path.Value.PSObject.Properties|Where-Object{$_.Name -in $methods}){
  $op=$entry.Value
  Heading 3 ($entry.Name.ToUpperInvariant()+' '+$path.Name) $op.operationId
  Paragraph $op.summary
  Paragraph $op.description
  if($op.parameters){
   $rows=[Collections.Generic.List[object]]::new()
   foreach($p in $op.parameters){$rows.Add(@($p.name,$p.in,(SchemaType $p.schema),$(if($p.required){'Yes'}else{'No'}),$p.description))}
   Table @('Parameter','Location','Type','Required','Description') $rows
  }
  if($op.requestBody){
   Heading 4 'Request body' ''
   Paragraph $(if($op.requestBody.required){'Required JSON body.'}else{'Optional JSON body.'})
   $media=@($op.requestBody.content.PSObject.Properties | Where-Object { $_.Name -eq 'application/json' })
   if(-not $media){$media=@($op.requestBody.content.PSObject.Properties | Select-Object -First 1)}
   MediaExamples $media[0].Value $document ('Request — '+$media[0].Name)
  }
  Heading 4 'Responses' ''
  $rows=[Collections.Generic.List[object]]::new()
  foreach($response in $op.responses.PSObject.Properties){$description=if($response.Name -in @('413','415','500')){'See Common errors.'}else{$response.Value.description};$rows.Add(@($response.Name,$description))}
  Table @('Status','Meaning') $rows
  foreach($response in $op.responses.PSObject.Properties){foreach($media in $response.Value.content.PSObject.Properties){MediaExamples $media.Value $document ('Response '+$response.Name+' — '+$media.Name)}}
 }}
 Heading 3 'Schemas' ''
 foreach($schema in $document.components.schemas.PSObject.Properties){
  Heading 4 $schema.Name ''
  if($schema.Value.description){Paragraph $schema.Value.description}
  if(-not $schema.Value.properties){Paragraph (SchemaType $schema.Value);continue}
  $rows=[Collections.Generic.List[object]]::new()
  foreach($p in $schema.Value.properties.PSObject.Properties){$description=if($p.Value.description){$p.Value.description}else{'—'};$rows.Add(@($p.Name,(SchemaType $p.Value),$(if($p.Name -in $schema.Value.required){'Yes'}else{'No'}),$description))}
  Table @('Field','Type','Required','Description') $rows
 }
}
New-Item -ItemType Directory -Path $schemas -Force | Out-Null
foreach($service in $services){[IO.File]::WriteAllText((Join-Path $schemas ($service.Key+'.openapi.json')),$service.Raw,$utf8)}
[IO.File]::WriteAllText((Join-Path $docs 'api-reference.md'),($md.ToString().TrimEnd()+"`n"),$utf8)
Write-Output 'Exported 17 operations to Markdown and 3 OpenAPI files. No business data changed.'
