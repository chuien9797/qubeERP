# QubeErp API reference

This reference describes the Product, Inventory and Order APIs, including request formats, responses and error handling. It is based on the exported OpenAPI contracts. Examples are illustrative, not captured command results; use IDs and versions returned by your environment.

See [README](../README.md) for setup and architecture, or the [HTTP walkthrough](reviewer-demo.http) for a complete example workflow.

<a id="conventions"></a>

## Calling conventions

Use JSON for command bodies. Preserve the original idempotency key or operation ID and payload on retries. A timed-out write may already have committed. GET endpoints are read-only.

For existing physical stock counts, send expectedVersion from GET inventory. A stale or missing version returns 409 STOCK_VERSION_CONFLICT without changing stock or movements. Refresh and review before submitting a new count; omit the version only when initialising absent stock.

Recover Pending orders through the original order POST. Use Order cancellation for order-owned reservations. Historical reservation quantities and movement balances are distinct from current inventory.

Product activation is independent of stock. Orders already validated before deactivation may still complete. Consumed and Fulfilled are compatibility states; there are no public consumption or fulfilment commands.

Errors use application/problem+json. Numeric status is the HTTP status; orderStatus, when present, is the saved order state. UTC timestamp fields use UTC offsets; Angular displays Malaysia time. The local demo has no authentication.

<a id="common-errors"></a>

## Common errors

| Status | Meaning |
| --- | --- |
| 413 | JSON-body endpoints: REQUEST_TOO_LARGE when the request exceeds 65,536 bytes. |
| 415 | JSON-body endpoints: UNSUPPORTED_MEDIA_TYPE; use Content-Type: application/json. |
| 500 | Unexpected server/database failure. ProblemDetails exposes an errorCode and traceId, not internal exception details. |

<a id="products"></a>

## QubeErp Products API

Create, retrieve and activate/deactivate catalogue products. Product status changes never modify inventory or existing orders. In-flight orders may still complete after deactivation. Example GUIDs illustrate request shapes; use IDs returned by your API.

[Live Swagger](http://localhost:5101/swagger) · [OpenAPI JSON](openapi/products.openapi.json)

| Method | Path | Purpose |
| --- | --- | --- |
| POST | /api/products | Create a product. |
| GET | /api/products | List products, including inactive products, in creation order. |
| PATCH | /api/products/{productId}/status | Activate or deactivate a catalogue product. |
| GET | /api/products/{id} | View a product. |
| POST | /api/products/lookup | Look up selected products. |

<a id="CreateProduct"></a>

### POST /api/products

Create a product.

Enter a required SKU (at most 50 characters), name (at most 200 characters) and nonnegative unit price with at most two decimal places. New products are active. The product ID is generated automatically. SKUs are trimmed and saved in uppercase. Creating a product does not initialise stock; set its stock count in InventoryService next.

#### Request body

Required JSON body.

Request — application/json

```json
{
  "sku": " mouse-01 ",
  "name": "Wireless Mouse",
  "description": "2.4 GHz",
  "unitPrice": 59.9
}
```

#### Responses

| Status | Meaning |
| --- | --- |
| 201 | Product created with a generated ID and active status. Location identifies the product. Stock is not initialised. |
| 400 | Invalid SKU, name, description or unit price. |
| 409 | DUPLICATE_SKU: the trimmed, uppercase SKU already exists, including a concurrent insert. |
| 500 | See Common errors. |
| 413 | See Common errors. |
| 415 | See Common errors. |

Response 201 — application/json

```json
{
  "id": "11111111-1111-1111-1111-111111111111",
  "sku": "MOUSE-01",
  "name": "Wireless Mouse",
  "description": "2.4 GHz",
  "unitPrice": 59.9,
  "isActive": true,
  "createdAtUtc": "2026-10-22T00:00:00Z",
  "updatedAtUtc": "2026-10-22T00:00:00Z"
}
```

Response 409 — application/problem+json

```json
{
  "title": "A product with this SKU already exists.",
  "status": 409,
  "errorCode": "DUPLICATE_SKU"
}
```

<a id="ListProducts"></a>

### GET /api/products

List products, including inactive products, in creation order.

Use offset and limit (up to 200) for pagination. Optional search matches SKU or name; optional isActive filters catalogue Active status, independently of stock availability. Filtering occurs before pagination. Defaults still return all products in creation order.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| offset | query | integer (int32) | No | Number of records to skip, starting at 0. Default 0. |
| limit | query | integer (int32) | No | Page size from 1 to 200. Default 100. |
| search | query | string | No | Optional SKU/name substring, trimmed, at most 100 characters. Applied before pagination. |
| isActive | query | boolean | No | Optional catalogue Active filter; this does not filter inventory availability. |

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | A page of products ordered by creation time then ID. Optional search matches SKU/name and isActive filters catalogue status before pagination. An empty page is an empty array. Stock availability is independent of Active status. |
| 400 | Invalid pagination or catalogue filter. |
| 500 | See Common errors. |

Response 200 — application/json

```json
[
  {
    "id": "11111111-1111-1111-1111-111111111111",
    "sku": "MOUSE-01",
    "name": "Wireless Mouse",
    "description": "2.4 GHz",
    "unitPrice": 59.9,
    "isActive": true,
    "createdAtUtc": "2026-10-22T00:00:00Z",
    "updatedAtUtc": "2026-10-22T00:00:00Z"
  }
]
```

<a id="SetProductStatus"></a>

### PATCH /api/products/{productId}/status

Activate or deactivate a catalogue product.

Send an explicit JSON boolean isActive. Returns 204 for a changed status or a same-value no-op; read the product for its currently observed status. Only IsActive and, on a real change, UpdatedAtUtc are written. Concurrent commands use last committed write wins, without ETags or cross-service locking. Deactivation blocks new orders whose Product lookup observes inactive; an already validated or durably Pending order may still finish/recover. Existing inventory, reservations, orders and movements are untouched. Zero stock never changes Active status. Direct Inventory reservation commands are not gated by catalogue status.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| productId | path | string (uuid) | Yes | Existing catalogue product ID. Empty GUID returns 400; unknown product returns 404. Malformed route GUIDs do not match this endpoint (404). |

#### Request body

Required JSON body.

Request — application/json

```json
{
  "isActive": false
}
```

#### Responses

| Status | Meaning |
| --- | --- |
| 204 | Status saved or already at the requested value. Same-value updates do not change UpdatedAtUtc. No response body. Concurrent commands use last committed write wins; read the product for current status. In-flight orders and accepted Pending recovery can still complete. |
| 400 | Invalid product ID or missing/non-boolean isActive. |
| 404 | No catalogue product exists for this ID. |
| 500 | See Common errors. |
| 413 | See Common errors. |
| 415 | See Common errors. |

<a id="GetProduct"></a>

### GET /api/products/{id}

View a product.

Retrieve a product by its ID. The response includes its generated ID, SKU, name, unit price and active status.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| id | path | string (uuid) | Yes | System-generated product ID returned by product creation. |

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | The stored product, including its current active status and catalogue price. |
| 404 | PRODUCT_NOT_FOUND: no product has this ID. |
| 500 | See Common errors. |

Response 200 — application/json

```json
{
  "id": "11111111-1111-1111-1111-111111111111",
  "sku": "MOUSE-01",
  "name": "Wireless Mouse",
  "description": "2.4 GHz",
  "unitPrice": 59.9,
  "isActive": true,
  "createdAtUtc": "2026-10-22T00:00:00Z",
  "updatedAtUtc": "2026-10-22T00:00:00Z"
}
```

<a id="LookupProducts"></a>

### POST /api/products/lookup

Look up selected products.

Supply 1–50 distinct product IDs. The response contains product details and any IDs that were not found. OrderService uses this endpoint to load catalogue prices.

#### Request body

Required JSON body.

Request — application/json

```json
{
  "productIds": [
    "11111111-1111-1111-1111-111111111111",
    "22222222-2222-2222-2222-222222222222"
  ]
}
```

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | Found products and missingProductIds. Missing or inactive products do not make this lookup an HTTP error. This POST is read-only. |
| 400 | Supply 1–50 distinct, nonempty product IDs. |
| 500 | See Common errors. |
| 413 | See Common errors. |
| 415 | See Common errors. |

Response 200 — application/json

```json
{
  "products": [],
  "missingProductIds": [
    "11111111-1111-1111-1111-111111111111"
  ]
}
```

### Schemas

#### CreateProductRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| sku | string | Yes | Catalogue SKU is trimmed, uppercased and unique; at most 50 characters after normalisation. Order lines retain the saved SKU. |
| name | string | Yes | Catalogue name is trimmed and at most 200 characters. Order lines retain the saved name. |
| description | string; nullable | No | — |
| unitPrice | number (decimal) | Yes | Nonnegative decimal with at most two fractional digits, up to 9999999999999999.99. Order lines retain the saved catalogue price. |

#### ProblemDetails

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| type | string; nullable | No | — |
| title | string; nullable | No | — |
| status | integer (int32); nullable | No | — |
| detail | string; nullable | No | — |
| instance | string; nullable | No | — |

#### ProductLookupRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| productIds | array of string (uuid) | Yes | — |

#### ProductLookupResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| products | array of ProductResponse | No | — |
| missingProductIds | array of string (uuid) | No | Requested IDs that do not exist. Inactive products are returned in products with isActive=false. |

#### ProductResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| id | string (uuid) | No | — |
| sku | string; nullable | No | Catalogue SKU is trimmed, uppercased and unique; at most 50 characters after normalisation. Order lines retain the saved SKU. |
| name | string; nullable | No | Catalogue name is trimmed and at most 200 characters. Order lines retain the saved name. |
| description | string; nullable | No | — |
| unitPrice | number (decimal) | No | Nonnegative decimal with at most two fractional digits, up to 9999999999999999.99. Order lines retain the saved catalogue price. |
| isActive | boolean | No | Catalogue activation, independent of stock availability. Status updates require an explicit boolean; zero stock never changes it. Deactivation does not fence orders already validated across services. |
| createdAtUtc | string (date-time) | No | — |
| updatedAtUtc | string (date-time) | No | — |

#### UpdateProductStatusRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| isActive | boolean | Yes | Catalogue activation, independent of stock availability. Status updates require an explicit boolean; zero stock never changes it. Deactivation does not fence orders already validated across services. |

#### ValidationProblemDetails

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| type | string; nullable | No | — |
| title | string; nullable | No | — |
| status | integer (int32); nullable | No | — |
| detail | string; nullable | No | — |
| instance | string; nullable | No | — |
| errors | object; nullable | No | — |

<a id="inventory"></a>

## QubeErp Inventory API

Maintain physical counts, receive stock and reserve multiple products atomically. Confirmed orders hold stock without shipping it. Example GUIDs illustrate request shapes; use IDs returned by your API.

[Live Swagger](http://localhost:5102/swagger) · [OpenAPI JSON](openapi/inventory.openapi.json)

| Method | Path | Purpose |
| --- | --- | --- |
| PUT | /api/inventory/{productId} | Set a product's physical stock count. |
| GET | /api/inventory/{productId} | Check a product's stock balance. |
| GET | /api/inventory/{productId}/movements | View a product's stock movements. |
| POST | /api/inventory/{productId}/receipts | Add received stock to a product. |
| POST | /api/inventory/reservations | Reserve stock for selected products. |
| POST | /api/inventory/reservations/{reservationId}/release | Release all stock held by a reservation. |
| GET | /api/inventory/reservations/{reservationId} | View a reservation's outcome and original product quantities. |

<a id="SetPhysicalStock"></a>

### PUT /api/inventory/{productId}

Set a product's physical stock count.

Supply the product ID, total physical quantity and reason. For existing stock, send expectedVersion from the last GET inventory response. A stale or omitted version returns 409 STOCK_VERSION_CONFLICT without changing stock or movements; reload and review the count before a new submission. Omit expectedVersion or send null only to initialise absent stock. Concurrent opening counts never overwrite the winning count. This replaces the previous count; it does not add a quantity. Use POST receipts to add newly received stock instead. Reserved stock stays unchanged, so the new count cannot be lower than Reserved. A product must exist before its first stock count is created. Absolute writes are not automatically retried after uncertain database errors. If the response is lost, inspect stock and movements before making a separate count; never assume failure or blindly retry.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| productId | path | string (uuid) | Yes | ID returned by ProductService. |

#### Request body

Required JSON body.

Request — application/json

```json
{
  "onHand": 10,
  "reason": "Physical count",
  "expectedVersion": "AAAAAAAAAAE="
}
```

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | Initialised or replaced the physical count; Reserved is preserved. An unchanged count creates no movement. |
| 400 | Invalid product ID, onHand, reason or expectedVersion. |
| 404 | STOCK_NOT_FOUND: the stock row was absent during the update. |
| 409 | STOCK_VERSION_CONFLICT: stock changed or an existing count omitted expectedVersion; reload and review before resubmission. STOCK_BELOW_RESERVED: the count is below Reserved. No stock or ledger changes commit. |
| 422 | PRODUCT_NOT_FOUND: a missing product cannot receive its first stock count. |
| 503 | PRODUCT_UNAVAILABLE: ProductService could not validate a first stock count. No stock was initialised. |
| 500 | See Common errors. |
| 413 | See Common errors. |
| 415 | See Common errors. |

Response 200 — application/json

```json
{
  "productId": "11111111-1111-1111-1111-111111111111",
  "onHand": 10,
  "reserved": 3,
  "availableQuantity": 7,
  "version": "AAAAAAAAAAE="
}
```

Response 409 — application/problem+json

```json
{
  "title": "Stock changed. Reload inventory and review the physical count before saving again.",
  "status": 409,
  "errorCode": "STOCK_VERSION_CONFLICT"
}
```

<a id="GetStockBalance"></a>

### GET /api/inventory/{productId}

Check a product's stock balance.

Retrieve OnHand, Reserved, AvailableQuantity and an opaque Base64 version. Use that version as expectedVersion for a subsequent physical count. Receipts, reservations, releases and changed physical counts invalidate previous versions. AvailableQuantity equals OnHand minus Reserved. A product without an initial stock count returns 404.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| productId | path | string (uuid) | Yes | ID returned by ProductService. |

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | Current physical and reserved quantities. availableQuantity is OnHand minus Reserved. |
| 400 | Product ID must not be empty. |
| 404 | STOCK_NOT_FOUND: inventory has not been initialised for this product. |
| 500 | See Common errors. |

Response 200 — application/json

```json
{
  "productId": "11111111-1111-1111-1111-111111111111",
  "onHand": 10,
  "reserved": 3,
  "availableQuantity": 7,
  "version": "AAAAAAAAAAE="
}
```

<a id="ListStockMovements"></a>

### GET /api/inventory/{productId}/movements

View a product's stock movements.

Retrieve opening stock, stocktake corrections, receipts, reservations and releases, oldest first by movement ID. Reservations increase Reserved; releases decrease Reserved. Both leave physical OnHand unchanged. Movement IDs identify audit rows, not orders. Reserve and Release use the original order/reservation ID as operationId. The After fields are historical balances immediately after that operation. This endpoint is read-only. Use offset and limit to page through the results.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| productId | path | string (uuid) | Yes | ID returned by ProductService. |
| offset | query | integer (int32) | No | Number of records to skip, starting at 0. Default 0. |
| limit | query | integer (int32) | No | Page size from 1 to 200. Default 100. |

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | Audit movements ordered by movement ID, oldest first. A product with zero opening stock can have an empty ledger. |
| 400 | Invalid product ID or pagination bounds. |
| 404 | STOCK_NOT_FOUND: inventory has not been initialised for this product. |
| 500 | See Common errors. |

Response 200 — application/json

```json
[
  {
    "id": 2,
    "operationId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
    "productId": "11111111-1111-1111-1111-111111111111",
    "movementType": "Reserve",
    "onHandDelta": 0,
    "reservedDelta": 3,
    "onHandAfter": 10,
    "reservedAfter": 3,
    "reason": null,
    "createdAtUtc": "2026-10-22T00:00:00Z"
  }
]
```

<a id="ReceiveStock"></a>

### POST /api/inventory/{productId}/receipts

Add received stock to a product.

Supply the product ID of initialised inventory and the additional quantity received. Existing stock is increased; Reserved stays unchanged. Supply a unique operation ID. Keep that ID and the same quantity and reason when retrying; a successful receipt is applied once. Use a new operation ID for a separate delivery. The response contains the original balances after this receipt, even on a later retry. Use PUT to initialise inventory or correct the total physical count.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| productId | path | string (uuid) | Yes | ID returned by ProductService. |

#### Request body

Required JSON body.

Request — application/json

```json
{
  "operationId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
  "quantity": 5,
  "reason": "Supplier delivery"
}
```

#### Responses

| Status | Meaning |
| --- | --- |
| 201 | Receipt committed once with its ledger entry. The response captures balances immediately after that receipt. |
| 200 | Same operation ID and payload replayed. Returns the original receipt snapshot; stock is not increased again. |
| 400 | Invalid product ID, operation ID, positive quantity or reason. |
| 404 | STOCK_NOT_FOUND: initialise inventory with PUT before receiving stock. |
| 409 | RECEIPT_PAYLOAD_CONFLICT for changed product/quantity/reason, or STOCK_CAPACITY_EXCEEDED for integer overflow. No additional stock change. |
| 503 | RECEIPT_UNAVAILABLE: the outcome could not be confirmed after SQL retries. Retry with the same operation ID and payload. |
| 500 | See Common errors. |
| 413 | See Common errors. |
| 415 | See Common errors. |

Response 201 — application/json

```json
{
  "operationId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
  "productId": "11111111-1111-1111-1111-111111111111",
  "quantity": 5,
  "reason": "Supplier delivery",
  "onHandAfter": 15,
  "reservedAfter": 3,
  "availableQuantityAfter": 12,
  "createdAtUtc": "2026-10-22T00:00:00Z"
}
```

Response 200 — application/json

```json
{
  "operationId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
  "productId": "11111111-1111-1111-1111-111111111111",
  "quantity": 5,
  "reason": "Supplier delivery",
  "onHandAfter": 15,
  "reservedAfter": 3,
  "availableQuantityAfter": 12,
  "createdAtUtc": "2026-10-22T00:00:00Z"
}
```

Response 409 — application/problem+json

```json
{
  "title": "Operation ID was already used for a different receipt.",
  "status": 409,
  "errorCode": "RECEIPT_PAYLOAD_CONFLICT"
}
```

<a id="ReserveStock"></a>

### POST /api/inventory/reservations

Reserve stock for selected products.

Supply a unique reservation ID and product quantities. Preserve the same ID and items when retrying; use a new ID for a separate reservation. If any product has insufficient stock, nothing is reserved and that request keeps its rejected result. OrderService normally calls this endpoint using the order ID. A direct manual reservation creates a standalone stock hold, not a sales order. Inventory checks its own stock rules and does not call ProductService to enforce catalogue Active status.

#### Request body

Required JSON body.

Request — application/json

```json
{
  "reservationId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "items": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "quantity": 3
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "quantity": 2
    }
  ]
}
```

#### Responses

| Status | Meaning |
| --- | --- |
| 201 | All items reserved atomically; one Reserve movement per item. Location identifies the reservation. |
| 200 | Successful original reservation replayed with no additional hold or movements. Its current status may be Reserved or Released; a terminal replay never reserves again. Consumed is recognised for compatibility but has no public command. |
| 400 | Invalid reservation ID or items; supply 1–50 distinct products with quantities of 1–10,000. |
| 409 | INSUFFICIENT_STOCK with reservationId and original shortfalls, or RESERVATION_PAYLOAD_CONFLICT for changed items. A shortage is durable and reserves nothing; replay stays rejected after restock. |
| 413 | See Common errors. |
| 503 | RESERVATION_UNAVAILABLE: reservation could not complete after bounded SQL deadlock retries, or its winning record could not be loaded. Keep the same ID when retrying. |
| 500 | See Common errors. |
| 415 | See Common errors. |

Response 201 — application/json

```json
{
  "reservationId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "status": "Reserved",
  "failureCode": null,
  "shortfalls": []
}
```

Response 200 — application/json

```json
{
  "reservationId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "status": "Reserved",
  "failureCode": null,
  "shortfalls": []
}
```

Response 409 — application/problem+json

```json
{
  "title": "Insufficient stock.",
  "status": 409,
  "errorCode": "INSUFFICIENT_STOCK",
  "reservationId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "shortfalls": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "requested": 3,
      "available": 1
    }
  ]
}
```

<a id="ReleaseReservation"></a>

### POST /api/inventory/reservations/{reservationId}/release

Release all stock held by a reservation.

Uses the original persisted lines and reservation ID. Released replays return 200 without another movement; Rejected or Consumed returns 409. OnHand is unchanged. The first committed release reason is retained in the ledger. For an order-owned hold, use OrderService cancellation so the order and inventory states are coordinated. Direct Inventory release does not update OrderDb.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| reservationId | path | string (uuid) | Yes | Original reservation ID; for an order reservation this is its orderId. Use the same ID to inspect a rejected result. |

#### Request body

Required JSON body.

Request — application/json

```json
{
  "reason": "Customer cancelled before shipment"
}
```

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | Released or already Released. Original persisted quantities are released once; OnHand is unchanged. First release reason stays in the ledger. |
| 400 | Invalid reservation ID or missing/invalid release reason. |
| 404 | RESERVATION_NOT_FOUND: no stored reservation outcome exists. |
| 409 | RESERVATION_NOT_RELEASABLE: Rejected, Consumed or another invalid state; no stock or ledger change. |
| 503 | RELEASE_UNAVAILABLE: bounded deadlock retries exhausted. Retry the same reservation identity. |
| 500 | See Common errors. |
| 413 | See Common errors. |
| 415 | See Common errors. |

Response 200 — application/json

```json
{
  "reservationId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "status": "Released",
  "failureCode": null,
  "shortfalls": []
}
```

<a id="GetReservation"></a>

### GET /api/inventory/reservations/{reservationId}

View a reservation's outcome and original product quantities.

Returns original persisted productId/quantity lines, ordered by product ID, for Reserved, Released, Consumed and Rejected outcomes. Quantities describe the historical request, not current stock balances. Rejected results retain their original failure code and shortfalls. This read never changes stock or contacts ProductService.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| reservationId | path | string (uuid) | Yes | Original reservation ID; for an order reservation this is its orderId. Use the same ID to inspect a rejected result. |

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | Stored Reserved, Released, Consumed or Rejected outcome with original productId/quantity items. These historical quantities are not current global Reserved balances. Rejected results retain their original failureCode and shortfalls. Read-only; no ProductService lookup. |
| 400 | Reservation ID must not be empty. |
| 404 | RESERVATION_NOT_FOUND with reservationId: no stored outcome exists for that ID. |
| 500 | See Common errors. |

Response 200 — application/json — Reserved: Original items held by the reservation

```json
{
  "reservationId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "status": "Reserved",
  "failureCode": null,
  "shortfalls": [],
  "items": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "quantity": 3
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "quantity": 2
    }
  ]
}
```

Response 200 — application/json — Released: Original items after release; quantities remain historical

```json
{
  "reservationId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "status": "Released",
  "failureCode": null,
  "shortfalls": [],
  "items": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "quantity": 3
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "quantity": 2
    }
  ]
}
```

Response 200 — application/json — Consumed: Compatibility state; no public consumption command

```json
{
  "reservationId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "status": "Consumed",
  "failureCode": null,
  "shortfalls": [],
  "items": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "quantity": 3
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "quantity": 2
    }
  ]
}
```

Response 200 — application/json — Rejected: Original request and persisted shortage

```json
{
  "reservationId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "status": "Rejected",
  "failureCode": "INSUFFICIENT_STOCK",
  "shortfalls": [
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "requested": 2,
      "available": 1
    }
  ],
  "items": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "quantity": 3
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "quantity": 2
    }
  ]
}
```

### Schemas

#### CreateReservationRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| reservationId | string (uuid) | Yes | Stable, nonempty request ID supplied by the caller. OrderService uses the order ID. Preserve it on retries. |
| items | array of ReservationItemRequest | Yes | 1–50 non-null lines with distinct, nonempty product IDs. Each line quantity must be 1–10,000. Item order does not affect the request fingerprint. |

#### InventoryResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| productId | string (uuid) | No | — |
| onHand | integer (int32) | No | Total physical count, not a quantity to add. Must be nonnegative and at least Reserved. |
| reserved | integer (int32) | No | Quantity held for confirmed orders or direct reservations; not a shipment count. |
| availableQuantity | integer (int32) | No | Current OnHand minus Reserved. |
| version | string; nullable | No | Opaque 8-byte SQL rowversion encoded as Base64. Changes on stock updates, including receipts, reservations and releases. This is not a timestamp. |

#### ProblemDetails

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| type | string; nullable | No | — |
| title | string; nullable | No | — |
| status | integer (int32); nullable | No | — |
| detail | string; nullable | No | — |
| instance | string; nullable | No | — |

#### ReceiveStockRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| operationId | string (uuid) | Yes | ID of the operation. Receipt IDs come from the caller; stocktake IDs are system-generated. Reserve and Release share the order/reservation ID; MovementType separates them. A ledger row's id is a separate audit identifier. |
| quantity | integer (int32) | Yes | Additional units received, from 1 to 2147483647, subject to the physical stock integer limit. |
| reason | string | Yes | Stocktake/receipt reason is trimmed and at most 300 characters. Release ledger reasons retain the accepted cancellation text, up to 500 characters. |

#### ReleaseReservationRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| reason | string | Yes | Required, nonblank reason of at most 500 characters. First accepted cancellation reason is immutable; replay text is ignored. |

#### ReservationDetailsResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| reservationId | string (uuid) | No | Stable, nonempty request ID supplied by the caller. OrderService uses the order ID. Preserve it on retries. |
| status | string; nullable | No | — |
| failureCode | string; nullable | No | — |
| shortfalls | array of StockShortfall | No | Original insufficient-stock details. Rejected replay does not recalculate availability after restock. |
| items | array of ReservationItemResponse | No | Original persisted request lines, sorted by product ID, including rejected requests. Quantities remain unchanged after release or consumption and do not describe current stock balances. |

#### ReservationItemRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| productId | string (uuid) | No | — |
| quantity | integer (int32) | No | Whole units requested, from 1 to 10,000. |

#### ReservationItemResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| productId | string (uuid) | No | — |
| quantity | integer (int32) | No | Whole units requested, from 1 to 10,000. |

#### ReservationResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| reservationId | string (uuid) | No | Stable, nonempty request ID supplied by the caller. OrderService uses the order ID. Preserve it on retries. |
| status | string; nullable | No | — |
| failureCode | string; nullable | No | — |
| shortfalls | array of StockShortfall | No | Original insufficient-stock details. Rejected replay does not recalculate availability after restock. |

#### SetStockRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| onHand | integer (int32) | Yes | Total physical count, not a quantity to add. Must be nonnegative and at least Reserved. |
| reason | string | Yes | Stocktake/receipt reason is trimmed and at most 300 characters. Release ledger reasons retain the accepted cancellation text, up to 500 characters. |
| expectedVersion | string; nullable | No | Copy version from GET inventory for an existing stock count. Missing or stale versions return 409 STOCK_VERSION_CONFLICT. Null or omitted means initialise only; malformed tokens return 400. Refresh and review the count after a conflict, without automatic resubmission. |

#### StockMovementResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| id | integer (int64) | No | — |
| operationId | string (uuid) | No | ID of the operation. Receipt IDs come from the caller; stocktake IDs are system-generated. Reserve and Release share the order/reservation ID; MovementType separates them. A ledger row's id is a separate audit identifier. |
| productId | string (uuid) | No | — |
| movementType | string; nullable | No | — |
| onHandDelta | integer (int32) | No | Signed change to physical stock made by this movement. Reserve movements use 0. |
| reservedDelta | integer (int32) | No | Signed change to held stock. Reserve adds the requested quantity; Release subtracts the original persisted quantity. |
| onHandAfter | integer (int32) | No | Physical stock immediately after this operation; later operations can change the current balance. |
| reservedAfter | integer (int32) | No | Reserved stock immediately after this operation. |
| reason | string; nullable | No | Stocktake/receipt reason is trimmed and at most 300 characters. Release ledger reasons retain the accepted cancellation text, up to 500 characters. |
| createdAtUtc | string (date-time) | No | — |

#### StockReceiptResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| operationId | string (uuid) | No | ID of the operation. Receipt IDs come from the caller; stocktake IDs are system-generated. Reserve and Release share the order/reservation ID; MovementType separates them. A ledger row's id is a separate audit identifier. |
| productId | string (uuid) | No | — |
| quantity | integer (int32) | No | Additional units received, from 1 to 2147483647, subject to the physical stock integer limit. |
| reason | string; nullable | No | Stocktake/receipt reason is trimmed and at most 300 characters. Release ledger reasons retain the accepted cancellation text, up to 500 characters. |
| onHandAfter | integer (int32) | No | Physical stock immediately after this operation; later operations can change the current balance. |
| reservedAfter | integer (int32) | No | Reserved stock immediately after this operation. |
| availableQuantityAfter | integer (int32) | No | OnHandAfter minus ReservedAfter at the time of this receipt, including on replay. |
| createdAtUtc | string (date-time) | No | — |

#### StockShortfall

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| productId | string (uuid) | No | — |
| requested | integer (int32) | No | — |
| available | integer (int32) | No | — |

#### ValidationProblemDetails

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| type | string; nullable | No | — |
| title | string; nullable | No | — |
| status | integer (int32); nullable | No | — |
| detail | string; nullable | No | — |
| instance | string; nullable | No | — |
| errors | object; nullable | No | — |

<a id="orders"></a>

## QubeErp Orders API

Create multi-item orders with saved prices and stable request keys. Recover uncertain Pending outcomes by replaying the original POST. Example GUIDs illustrate request shapes; use IDs returned by your API.

[Live Swagger](http://localhost:5103/swagger) · [OpenAPI JSON](openapi/orders.openapi.json)

| Method | Path | Purpose |
| --- | --- | --- |
| POST | /api/orders | Create an order for selected products. |
| GET | /api/orders | List saved orders, optionally filtered by status. |
| POST | /api/orders/{id}/cancel | Cancel a confirmed order and release its reserved stock. |
| POST | /api/orders/{id}/reconcile-cancellation | Recover an uncertain cancellation using the original reservation. |
| GET | /api/orders/{id} | View an order and its saved prices. |

<a id="CreateOrder"></a>

### POST /api/orders

Create an order for selected products.

Supply 1–50 distinct active products with quantities of 1–10,000 and an Idempotency-Key header. Preserve the key and items to retry the same order or recover Pending. Use a new key for a separate purchase. OrderService generates the order ID and saves catalogue names, SKUs and prices. Confirmation reserves stock without reducing physical OnHand. A shortage rejects the whole order. A 202 response means the outcome is uncertain; retry the same key and items. Order error responses use numeric status for the HTTP code and orderStatus for the saved order state. Successful/Pending responses and GET order retain their string status field.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| Idempotency-Key | header | string | Yes | Required stable key for one purchase: one nonblank header, at most 128 characters. Trimmed and case-sensitive. Keep the key and identical items when retrying or recovering Pending. Changed items return 409; a new purchase needs a new key. HTTP clients supply and preserve their own key. |

#### Request body

Required JSON body.

Request — application/json

```json
{
  "items": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "quantity": 3
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "quantity": 2
    }
  ]
}
```

#### Responses

| Status | Meaning |
| --- | --- |
| 201 | New order Confirmed with saved product/price snapshots. Stock is reserved, not shipped. Location identifies the order. |
| 200 | Existing order is Confirmed, Cancelled or Fulfilled, including a successful Pending recovery. Returns saved snapshots without another logical stock hold. Fulfilled is recognised for compatibility but has no public command. |
| 202 | Pending: Inventory's outcome is uncertain, including an Inventory HTTP 408. Response contains orderId, status and statusUrl; Location identifies the order. Repeat the same POST key/items to recover; GET alone does not recover. Recovery verifies original product IDs and quantities from the saved reservation; mismatched or missing lines require review. Replaying a key while its order is CancellationPending returns that state; use cancel or reconcile-cancellation to recover cancellation. |
| 400 | Invalid Idempotency-Key or items; supply 1–50 distinct products with quantities of 1–10,000. |
| 409 | INSUFFICIENT_STOCK with orderId, orderStatus, statusUrl and original shortfalls, or IDEMPOTENCY_KEY_CONFLICT for a changed payload. Numeric status is the HTTP code. No additional stock hold. |
| 413 | See Common errors. |
| 422 | INVALID_PRODUCT with invalidProductIds for missing/inactive products, or ORDER_TOTAL_OUT_OF_RANGE. No new order or reservation created. |
| 502 | Inventory contract failure: stored Rejected/INVENTORY_INVALID_REQUEST or RequiresReview/RESERVATION_PAYLOAD_CONFLICT/INVENTORY_UNEXPECTED_RESPONSE. Inspect orderId/statusUrl; do not treat as confirmed. |
| 503 | PRODUCT_UNAVAILABLE: catalogue lookup failed or returned unusable data before a new order was saved. |
| 500 | See Common errors. |
| 415 | See Common errors. |

Response 201 — application/json

```json
{
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "status": "Confirmed",
  "totalAmount": 199.5,
  "lines": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "sku": "MOUSE-01",
      "name": "Wireless Mouse",
      "unitPrice": 59.9,
      "quantity": 3,
      "lineTotal": 179.7
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "sku": "PAD-01",
      "name": "Mouse Pad",
      "unitPrice": 9.9,
      "quantity": 2,
      "lineTotal": 19.8
    }
  ],
  "failureCode": null,
  "shortfalls": [],
  "createdAtUtc": "2026-10-22T00:00:00Z",
  "updatedAtUtc": "2026-10-22T00:00:01Z",
  "lastAttemptAtUtc": "2026-10-22T00:00:00Z",
  "cancellationReason": null
}
```

Response 200 — application/json

```json
{
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "status": "Confirmed",
  "totalAmount": 199.5,
  "lines": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "sku": "MOUSE-01",
      "name": "Wireless Mouse",
      "unitPrice": 59.9,
      "quantity": 3,
      "lineTotal": 179.7
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "sku": "PAD-01",
      "name": "Mouse Pad",
      "unitPrice": 9.9,
      "quantity": 2,
      "lineTotal": 19.8
    }
  ],
  "failureCode": null,
  "shortfalls": [],
  "createdAtUtc": "2026-10-22T00:00:00Z",
  "updatedAtUtc": "2026-10-22T00:00:01Z",
  "lastAttemptAtUtc": "2026-10-22T00:00:00Z",
  "cancellationReason": null
}
```

Response 202 — application/json

```json
{
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "status": "Pending",
  "statusUrl": "/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
}
```

Response 409 — application/problem+json

```json
{
  "title": "Insufficient stock.",
  "status": 409,
  "errorCode": "INSUFFICIENT_STOCK",
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "statusUrl": "/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "orderStatus": "Rejected",
  "shortfalls": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "requested": 3,
      "available": 1
    }
  ]
}
```

Response 422 — application/problem+json

```json
{
  "title": "Some products are missing or inactive.",
  "status": 422,
  "errorCode": "INVALID_PRODUCT",
  "invalidProductIds": [
    "11111111-1111-1111-1111-111111111111"
  ]
}
```

Response 502 — application/problem+json

```json
{
  "title": "The Inventory response needs investigation.",
  "status": 502,
  "errorCode": "INVENTORY_UNEXPECTED_RESPONSE",
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "orderStatus": "RequiresReview",
  "statusUrl": "/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
}
```

Response 503 — application/problem+json

```json
{
  "title": "Product lookup is unavailable.",
  "status": 503,
  "errorCode": "PRODUCT_UNAVAILABLE"
}
```

<a id="ListOrders"></a>

### GET /api/orders

List saved orders, optionally filtered by status.

Use status=RequiresReview for manual investigation. Optional newestFirst=true returns recent orders first; the default remains oldest first. Filtering and sorting precede pagination. This read never reconciles orders or changes stock.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| status | query | string: Pending, Confirmed, Rejected, RequiresReview, CancellationPending, Cancelled, Fulfilled | No | Optional saved order state. Leave unselected for all orders; select RequiresReview for manual investigation. Listing never reconciles orders or changes stock. |
| offset | query | integer (int32) | No | Number of records to skip, starting at 0. Default 0. |
| limit | query | integer (int32) | No | Page size from 1 to 200. Default 100. |
| newestFirst | query | boolean | No | Set true for descending creation time and ID. Defaults to false for compatibility. |

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | Saved orders ordered by creation time and ID, or descending when newestFirst=true. Filter status=RequiresReview for manual investigation. Read-only. |
| 400 | Invalid status filter or pagination bounds. |
| 500 | See Common errors. |

Response 200 — application/json

```json
[
  {
    "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
    "status": "Confirmed",
    "totalAmount": 199.5,
    "lines": [
      {
        "productId": "11111111-1111-1111-1111-111111111111",
        "sku": "MOUSE-01",
        "name": "Wireless Mouse",
        "unitPrice": 59.9,
        "quantity": 3,
        "lineTotal": 179.7
      },
      {
        "productId": "22222222-2222-2222-2222-222222222222",
        "sku": "PAD-01",
        "name": "Mouse Pad",
        "unitPrice": 9.9,
        "quantity": 2,
        "lineTotal": 19.8
      }
    ],
    "failureCode": null,
    "shortfalls": [],
    "createdAtUtc": "2026-10-22T00:00:00Z",
    "updatedAtUtc": "2026-10-22T00:00:01Z",
    "lastAttemptAtUtc": "2026-10-22T00:00:00Z",
    "cancellationReason": null
  }
]
```

<a id="CancelOrder"></a>

### POST /api/orders/{id}/cancel

Cancel a confirmed order and release its reserved stock.

Requires a reason of 1–500 characters. The first accepted reason is immutable. Replays use the same reservation identity and saved reason. 202 means release is uncertain; retry cancel or reconcile cancellation. Pending orders must reconcile their initial reservation first. Inventory contradictions require manual review.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| id | path | string (uuid) | Yes | System-generated orderId returned by order creation. GET does not trigger Pending recovery. |

#### Request body

Required JSON body.

Request — application/json

```json
{
  "reason": "Customer cancelled before shipment"
}
```

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | Cancelled, including idempotent replay. All held quantities released once; original cancellationReason retained. |
| 202 | CancellationPending: durable intent saved, release outcome uncertain. Retry cancel or reconcile-cancellation with the original order ID. |
| 400 | Invalid order ID or missing/invalid cancellation reason. |
| 404 | ORDER_NOT_FOUND: no order has this ID. |
| 409 | Invalid order state or RequiresReview. Pending orders must reconcile reservation first. Unexpected Inventory 4xx requires manual review; orderStatus is separate from numeric HTTP status. |
| 500 | See Common errors. |
| 413 | See Common errors. |
| 415 | See Common errors. |

Response 200 — application/json

```json
{
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "status": "Cancelled",
  "totalAmount": 199.5,
  "lines": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "sku": "MOUSE-01",
      "name": "Wireless Mouse",
      "unitPrice": 59.9,
      "quantity": 3,
      "lineTotal": 179.7
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "sku": "PAD-01",
      "name": "Mouse Pad",
      "unitPrice": 9.9,
      "quantity": 2,
      "lineTotal": 19.8
    }
  ],
  "failureCode": null,
  "shortfalls": [],
  "createdAtUtc": "2026-10-22T00:00:00Z",
  "updatedAtUtc": "2026-10-22T00:00:01Z",
  "lastAttemptAtUtc": "2026-10-22T00:00:00Z",
  "cancellationReason": "Customer cancelled before shipment"
}
```

Response 202 — application/json

```json
{
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "status": "CancellationPending",
  "statusUrl": "/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
}
```

Response 409 — application/problem+json

```json
{
  "title": "Cancellation requires manual review.",
  "status": 409,
  "errorCode": "CANCELLATION_REQUIRES_REVIEW",
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "orderStatus": "RequiresReview",
  "statusUrl": "/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
}
```

<a id="ReconcileCancellation"></a>

### POST /api/orders/{id}/reconcile-cancellation

Recover an uncertain cancellation using the original reservation.

Queries Inventory first. Verified Released completes cancellation; Reserved re-drives release with the saved reason and original ID. Timeouts remain CancellationPending. RequiresReview never transitions automatically. Initial Pending reservation recovery remains the original order POST.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| id | path | string (uuid) | Yes | System-generated orderId returned by order creation. GET does not trigger Pending recovery. |

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | Released verified; order Cancelled with one OrderCancelled outbox event. Already Cancelled replays without Inventory calls. |
| 202 | Inventory query or release remains uncertain. CancellationPending is preserved. |
| 404 | ORDER_NOT_FOUND. |
| 409 | Order is not CancellationPending/Cancelled, or Inventory contradicts cancellation. RequiresReview has no automatic transition. |
| 500 | See Common errors. |

Response 200 — application/json

```json
{
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "status": "Cancelled",
  "totalAmount": 199.5,
  "lines": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "sku": "MOUSE-01",
      "name": "Wireless Mouse",
      "unitPrice": 59.9,
      "quantity": 3,
      "lineTotal": 179.7
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "sku": "PAD-01",
      "name": "Mouse Pad",
      "unitPrice": 9.9,
      "quantity": 2,
      "lineTotal": 19.8
    }
  ],
  "failureCode": null,
  "shortfalls": [],
  "createdAtUtc": "2026-10-22T00:00:00Z",
  "updatedAtUtc": "2026-10-22T00:00:01Z",
  "lastAttemptAtUtc": "2026-10-22T00:00:00Z",
  "cancellationReason": "Customer cancelled before shipment"
}
```

Response 202 — application/json

```json
{
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "status": "CancellationPending",
  "statusUrl": "/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
}
```

Response 409 — application/problem+json

```json
{
  "title": "Cancellation requires manual review.",
  "status": 409,
  "errorCode": "CANCELLATION_REQUIRES_REVIEW",
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "orderStatus": "RequiresReview",
  "statusUrl": "/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
}
```

<a id="GetOrder"></a>

### GET /api/orders/{id}

View an order and its saved prices.

Retrieve a stored order by ID. Reading an order does not retry reservations or change its status. To recover a Pending order, repeat its original POST with the same request key and items.

| Parameter | Location | Type | Required | Description |
| --- | --- | --- | --- | --- |
| id | path | string (uuid) | Yes | System-generated orderId returned by order creation. GET does not trigger Pending recovery. |

#### Responses

| Status | Meaning |
| --- | --- |
| 200 | Stored order, item snapshots and cancellationReason. This read has no recovery side effects. |
| 404 | ORDER_NOT_FOUND: no order has this ID. |
| 500 | See Common errors. |

Response 200 — application/json

```json
{
  "orderId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "status": "Confirmed",
  "totalAmount": 199.5,
  "lines": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "sku": "MOUSE-01",
      "name": "Wireless Mouse",
      "unitPrice": 59.9,
      "quantity": 3,
      "lineTotal": 179.7
    },
    {
      "productId": "22222222-2222-2222-2222-222222222222",
      "sku": "PAD-01",
      "name": "Mouse Pad",
      "unitPrice": 9.9,
      "quantity": 2,
      "lineTotal": 19.8
    }
  ],
  "failureCode": null,
  "shortfalls": [],
  "createdAtUtc": "2026-10-22T00:00:00Z",
  "updatedAtUtc": "2026-10-22T00:00:01Z",
  "lastAttemptAtUtc": "2026-10-22T00:00:00Z",
  "cancellationReason": null
}
```

### Schemas

#### CancelOrderRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| reason | string | Yes | Required, nonblank reason of at most 500 characters. First accepted cancellation reason is immutable; replay text is ignored. |

#### CreateOrderRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| items | array of OrderItemRequest | Yes | 1–50 non-null lines with distinct, nonempty product IDs. Each line quantity must be 1–10,000. Item order does not affect the request fingerprint. |

#### OrderItemRequest

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| productId | string (uuid) | No | — |
| quantity | integer (int32) | No | Whole units requested, from 1 to 10,000. |

#### OrderLineResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| productId | string (uuid) | No | — |
| sku | string; nullable | No | Catalogue SKU is trimmed, uppercased and unique; at most 50 characters after normalisation. Order lines retain the saved SKU. |
| name | string; nullable | No | Catalogue name is trimmed and at most 200 characters. Order lines retain the saved name. |
| unitPrice | number (decimal) | No | Nonnegative decimal with at most two fractional digits, up to 9999999999999999.99. Order lines retain the saved catalogue price. |
| quantity | integer (int32) | No | Whole units requested, from 1 to 10,000. |
| lineTotal | number (decimal) | No | Decimal unit price multiplied by quantity, stored with two fractional digits. |

#### OrderResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| orderId | string (uuid) | No | — |
| status | string; nullable | No | — |
| totalAmount | number (decimal) | No | Sum of saved decimal line totals, within the decimal(18,2) storage range. |
| lines | array of OrderLineResponse | No | — |
| failureCode | string; nullable | No | — |
| shortfalls | array of StockShortfall | No | Original insufficient-stock details. Rejected replay does not recalculate availability after restock. |
| createdAtUtc | string (date-time) | No | — |
| updatedAtUtc | string (date-time) | No | — |
| lastAttemptAtUtc | string (date-time); nullable | No | — |
| cancellationReason | string; nullable | No | First accepted cancellation reason; preserved on all retries, including different replay text. Null before cancellation. |

#### PendingOrderResponse

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| orderId | string (uuid) | No | — |
| status | string; nullable | No | — |
| statusUrl | string; nullable | No | — |

#### ProblemDetails

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| type | string; nullable | No | — |
| title | string; nullable | No | — |
| status | integer (int32); nullable | No | — |
| detail | string; nullable | No | — |
| instance | string; nullable | No | — |

#### StockShortfall

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| productId | string (uuid) | No | — |
| requested | integer (int32) | No | — |
| available | integer (int32) | No | — |

#### ValidationProblemDetails

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| type | string; nullable | No | — |
| title | string; nullable | No | — |
| status | integer (int32); nullable | No | — |
| detail | string; nullable | No | — |
| instance | string; nullable | No | — |
| errors | object; nullable | No | — |
