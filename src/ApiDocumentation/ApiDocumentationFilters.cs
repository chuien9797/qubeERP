using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Reflection;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace QubeERP.ApiDocumentation;

public static class ApiDocumentationConfiguration
{
    public static void AddQubeApiDocumentation(this SwaggerGenOptions options, string service, string description)
    {
        options.MapType<decimal>(() => new OpenApiSchema { Type = JsonSchemaType.Number, Format = "decimal" });
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = $"QubeErp {service} API",
            Version = "v1",
            Description = description + " Example GUIDs illustrate request shapes; use IDs returned by your API."
        });
        options.OperationFilter<ApiOperationDocumentation>();
        options.SchemaFilter<ApiSchemaDocumentation>();
    }
}

public sealed class ApiOperationDocumentation : IOperationFilter
{
    private sealed record Endpoint(string OperationId, Dictionary<string, string> Responses);

    private static readonly Dictionary<string, Endpoint> Endpoints = new()
    {
        ["ProductsController.Create"] = new("CreateProduct", new()
        {
            ["201"] = "Product created with a generated ID and active status. Location identifies the product. Stock is not initialised.",
            ["409"] = "DUPLICATE_SKU: the trimmed, uppercase SKU already exists, including a concurrent insert."
        }),
        ["ProductsController.List"] = new("ListProducts", new()
        {
            ["200"] = "A page of products ordered by creation time then ID. Optional search matches SKU/name and isActive filters catalogue status before pagination. An empty page is an empty array. Stock availability is independent of Active status."
        }),
        ["ProductsController.SetStatus"] = new("SetProductStatus", new()
        {
            ["204"] = "Status saved or already at the requested value. Same-value updates do not change UpdatedAtUtc. No response body. Concurrent commands use last committed write wins; read the product for current status. In-flight orders and accepted Pending recovery can still complete.",
            ["404"] = "No catalogue product exists for this ID."
        }),
        ["ProductsController.Get"] = new("GetProduct", new()
        {
            ["200"] = "The stored product, including its current active status and catalogue price.",
            ["404"] = "PRODUCT_NOT_FOUND: no product has this ID."
        }),
        ["ProductsController.Lookup"] = new("LookupProducts", new()
        {
            ["200"] = "Found products and missingProductIds. Missing or inactive products do not make this lookup an HTTP error. This POST is read-only."
        }),
        ["InventoryController.Set"] = new("SetPhysicalStock", new()
        {
            ["200"] = "Initialised or replaced the physical count; Reserved is preserved. An unchanged count creates no movement.",
            ["404"] = "STOCK_NOT_FOUND: the stock row was absent during the update.",
            ["409"] = "STOCK_VERSION_CONFLICT: stock changed or an existing count omitted expectedVersion; reload and review before resubmission. STOCK_BELOW_RESERVED: the count is below Reserved. No stock or ledger changes commit.",
            ["422"] = "PRODUCT_NOT_FOUND: a missing product cannot receive its first stock count.",
            ["503"] = "PRODUCT_UNAVAILABLE: ProductService could not validate a first stock count. No stock was initialised."
        }),
        ["InventoryController.Get"] = new("GetStockBalance", new()
        {
            ["200"] = "Current physical and reserved quantities. availableQuantity is OnHand minus Reserved.",
            ["404"] = "STOCK_NOT_FOUND: inventory has not been initialised for this product."
        }),
        ["InventoryController.Movements"] = new("ListStockMovements", new()
        {
            ["200"] = "Audit movements ordered by movement ID, oldest first. A product with zero opening stock can have an empty ledger.",
            ["404"] = "STOCK_NOT_FOUND: inventory has not been initialised for this product."
        }),
        ["StockReceiptsController.Receive"] = new("ReceiveStock", new()
        {
            ["201"] = "Receipt committed once with its ledger entry. The response captures balances immediately after that receipt.",
            ["200"] = "Same operation ID and payload replayed. Returns the original receipt snapshot; stock is not increased again.",
            ["404"] = "STOCK_NOT_FOUND: initialise inventory with PUT before receiving stock.",
            ["409"] = "RECEIPT_PAYLOAD_CONFLICT for changed product/quantity/reason, or STOCK_CAPACITY_EXCEEDED for integer overflow. No additional stock change.",
            ["503"] = "RECEIPT_UNAVAILABLE: the outcome could not be confirmed after SQL retries. Retry with the same operation ID and payload."
        }),
        ["ReservationsController.Create"] = new("ReserveStock", new()
        {
            ["201"] = "All items reserved atomically; one Reserve movement per item. Location identifies the reservation.",
            ["200"] = "Successful original reservation replayed with no additional hold or movements. Its current status may be Reserved or Released; a terminal replay never reserves again. Consumed is recognised for compatibility but has no public command.",
            ["409"] = "INSUFFICIENT_STOCK with reservationId and original shortfalls, or RESERVATION_PAYLOAD_CONFLICT for changed items. A shortage is durable and reserves nothing; replay stays rejected after restock.",
            ["503"] = "RESERVATION_UNAVAILABLE: reservation could not complete after bounded SQL deadlock retries, or its winning record could not be loaded. Keep the same ID when retrying."
        }),
        ["ReservationsController.Get"] = new("GetReservation", new()
        {
            ["200"] = "Stored Reserved, Released, Consumed or Rejected outcome with original productId/quantity items. These historical quantities are not current global Reserved balances. Rejected results retain their original failureCode and shortfalls. Read-only; no ProductService lookup.",
            ["404"] = "RESERVATION_NOT_FOUND with reservationId: no stored outcome exists for that ID."
        }),
        ["ReservationsController.Release"] = new("ReleaseReservation", new()
        {
            ["200"] = "Released or already Released. Original persisted quantities are released once; OnHand is unchanged. First release reason stays in the ledger.",
            ["404"] = "RESERVATION_NOT_FOUND: no stored reservation outcome exists.",
            ["409"] = "RESERVATION_NOT_RELEASABLE: Rejected, Consumed or another invalid state; no stock or ledger change.",
            ["503"] = "RELEASE_UNAVAILABLE: bounded deadlock retries exhausted. Retry the same reservation identity."
        }),
        ["OrdersController.Cancel"] = new("CancelOrder", new()
        {
            ["200"] = "Cancelled, including idempotent replay. All held quantities released once; original cancellationReason retained.",
            ["202"] = "CancellationPending: durable intent saved, release outcome uncertain. Retry cancel or reconcile-cancellation with the original order ID.",
            ["404"] = "ORDER_NOT_FOUND: no order has this ID.",
            ["409"] = "Invalid order state or RequiresReview. Pending orders must reconcile reservation first. Unexpected Inventory 4xx requires manual review; orderStatus is separate from numeric HTTP status."
        }),
        ["OrdersController.ReconcileCancellation"] = new("ReconcileCancellation", new()
        {
            ["200"] = "Released verified; order Cancelled with one OrderCancelled outbox event. Already Cancelled replays without Inventory calls.",
            ["202"] = "Inventory query or release remains uncertain. CancellationPending is preserved.",
            ["404"] = "ORDER_NOT_FOUND.",
            ["409"] = "Order is not CancellationPending/Cancelled, or Inventory contradicts cancellation. RequiresReview has no automatic transition."
        }),
        ["OrdersController.List"] = new("ListOrders", new()
        {
            ["200"] = "Saved orders ordered by creation time and ID, or descending when newestFirst=true. Filter status=RequiresReview for manual investigation. Read-only."
        }),
        ["OrdersController.Create"] = new("CreateOrder", new()
        {
            ["201"] = "New order Confirmed with saved product/price snapshots. Stock is reserved, not shipped. Location identifies the order.",
            ["200"] = "Existing order is Confirmed, Cancelled or Fulfilled, including a successful Pending recovery. Returns saved snapshots without another logical stock hold. Fulfilled is recognised for compatibility but has no public command.",
            ["202"] = "Pending: Inventory's outcome is uncertain, including an Inventory HTTP 408. Response contains orderId, status and statusUrl; Location identifies the order. Repeat the same POST key/items to recover; GET alone does not recover. Recovery verifies original product IDs and quantities from the saved reservation; mismatched or missing lines require review. Replaying a key while its order is CancellationPending returns that state; use cancel or reconcile-cancellation to recover cancellation.",
            ["409"] = "INSUFFICIENT_STOCK with orderId, orderStatus, statusUrl and original shortfalls, or IDEMPOTENCY_KEY_CONFLICT for a changed payload. Numeric status is the HTTP code. No additional stock hold.",
            ["422"] = "INVALID_PRODUCT with invalidProductIds for missing/inactive products, or ORDER_TOTAL_OUT_OF_RANGE. No new order or reservation created.",
            ["502"] = "Inventory contract failure: stored Rejected/INVENTORY_INVALID_REQUEST or RequiresReview/RESERVATION_PAYLOAD_CONFLICT/INVENTORY_UNEXPECTED_RESPONSE. Inspect orderId/statusUrl; do not treat as confirmed.",
            ["503"] = "PRODUCT_UNAVAILABLE: catalogue lookup failed or returned unusable data before a new order was saved."
        }),
        ["OrdersController.Get"] = new("GetOrder", new()
        {
            ["200"] = "Stored order, item snapshots and cancellationReason. This read has no recovery side effects.",
            ["404"] = "ORDER_NOT_FOUND: no order has this ID."
        })
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var key = $"{context.MethodInfo.DeclaringType?.Name}.{context.MethodInfo.Name}";
        if (!Endpoints.TryGetValue(key, out var endpoint)) return;
        operation.OperationId = endpoint.OperationId;
        if (operation.RequestBody is OpenApiRequestBody requestBody)
        {
            var nullability = new NullabilityInfoContext();
            requestBody.Required = context.ApiDescription.ParameterDescriptions
                .Where(parameter => parameter.Source == BindingSource.Body)
                .Any(parameter => parameter.IsRequired || parameter.ModelMetadata?.IsRequired == true
                    || context.MethodInfo.GetParameters().Any(methodParameter => methodParameter.Name == parameter.Name
                        && nullability.Create(methodParameter).ReadState == NullabilityState.NotNull));
        }
        if (key == "OrdersController.Create")
            operation.Description += " Order error responses use numeric status for the HTTP code and orderStatus for the saved order state. Successful/Pending responses and GET order retain their string status field.";
        operation.Responses ??= new OpenApiResponses();
        foreach (var (status, description) in endpoint.Responses)
            if (operation.Responses.TryGetValue(status, out var response) && response is OpenApiResponse mutable)
                mutable.Description = description;

        var conflictExample = key switch
        {
            "ProductsController.Create" => """{"title":"A product with this SKU already exists.","status":409,"errorCode":"DUPLICATE_SKU"}""",
            "InventoryController.Set" => """{"title":"Stock changed. Reload inventory and review the physical count before saving again.","status":409,"errorCode":"STOCK_VERSION_CONFLICT"}""",
            "StockReceiptsController.Receive" => """{"title":"Operation ID was already used for a different receipt.","status":409,"errorCode":"RECEIPT_PAYLOAD_CONFLICT"}""",
            "ReservationsController.Create" => """{"title":"Insufficient stock.","status":409,"errorCode":"INSUFFICIENT_STOCK","reservationId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","shortfalls":[{"productId":"11111111-1111-1111-1111-111111111111","requested":3,"available":1}]}""",
            "OrdersController.Create" => """{"title":"Insufficient stock.","status":409,"errorCode":"INSUFFICIENT_STOCK","orderId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","statusUrl":"/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","orderStatus":"Rejected","shortfalls":[{"productId":"11111111-1111-1111-1111-111111111111","requested":3,"available":1}]}""",
            "OrdersController.Cancel" or "OrdersController.ReconcileCancellation" => """{"title":"Cancellation requires manual review.","status":409,"errorCode":"CANCELLATION_REQUIRES_REVIEW","orderId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","orderStatus":"RequiresReview","statusUrl":"/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}""",
            _ => null
        };
        if (conflictExample is not null && operation.Responses.TryGetValue("409", out var conflict)
            && conflict is OpenApiResponse { Content: not null } conflictResponse)
            foreach (var media in conflictResponse.Content.Values)
                media.Example = JsonNode.Parse(conflictExample);

        if (key == "OrdersController.Create")
        {
            SetResponseExample(operation, "201", JsonNode.Parse(ApiSchemaDocumentation.OrderResponseExample)!);
            SetResponseExample(operation, "200", JsonNode.Parse(ApiSchemaDocumentation.OrderResponseExample)!);
            SetResponseExample(operation, "202", JsonNode.Parse("""{"orderId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","status":"Pending","statusUrl":"/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}""")!);
            SetResponseExample(operation, "422", JsonNode.Parse("""{"title":"Some products are missing or inactive.","status":422,"errorCode":"INVALID_PRODUCT","invalidProductIds":["11111111-1111-1111-1111-111111111111"]}""")!);
            SetResponseExample(operation, "502", JsonNode.Parse("""{"title":"The Inventory response needs investigation.","status":502,"errorCode":"INVENTORY_UNEXPECTED_RESPONSE","orderId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","orderStatus":"RequiresReview","statusUrl":"/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}""")!);
            SetResponseExample(operation, "503", JsonNode.Parse("""{"title":"Product lookup is unavailable.","status":503,"errorCode":"PRODUCT_UNAVAILABLE"}""")!);
        }

        if (key == "ReservationsController.Release")
            SetResponseExample(operation, "200", JsonNode.Parse("""{"reservationId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","status":"Released","failureCode":null,"shortfalls":[]}""")!);

        if (key == "ReservationsController.Get" && operation.Responses.TryGetValue("200", out var details)
            && details is OpenApiResponse { Content: not null } detailsResponse)
        {
            foreach (var media in detailsResponse.Content.Values)
            {
                media.Example = null;
                media.Examples = new Dictionary<string, IOpenApiExample>();
                foreach (var state in new[] { "Reserved", "Released", "Consumed", "Rejected" })
                {
                    media.Examples[state] = new OpenApiExample
                    {
                        Summary = state == "Rejected" ? "Original request and persisted shortage"
                            : state == "Reserved" ? "Original items held by the reservation"
                            : state == "Released" ? "Original items after release; quantities remain historical"
                            : "Compatibility state; no public consumption command",
                        Value = ApiSchemaDocumentation.ReservationExample(state)
                    };
                }
            }
        }

        if (key is "OrdersController.Cancel" or "OrdersController.ReconcileCancellation")
        {
            var cancelled = JsonNode.Parse(ApiSchemaDocumentation.OrderResponseExample)!;
            cancelled["status"] = "Cancelled";
            cancelled["cancellationReason"] = "Customer cancelled before shipment";
            SetResponseExample(operation, "200", cancelled);
            SetResponseExample(operation, "202", JsonNode.Parse("""{"orderId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","status":"CancellationPending","statusUrl":"/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}""")!);
        }

        if (operation.Responses.TryGetValue("400", out var invalid) && invalid is OpenApiResponse validation)
            validation.Description = key switch
            {
                "ProductsController.Create" => "Invalid SKU, name, description or unit price.",
                "ProductsController.List" => "Invalid pagination or catalogue filter.",
                "ProductsController.Get" => "Invalid product ID.",
                "ProductsController.SetStatus" => "Invalid product ID or missing/non-boolean isActive.",
                "ProductsController.Lookup" => "Supply 1–50 distinct, nonempty product IDs.",
                "InventoryController.Set" => "Invalid product ID, onHand, reason or expectedVersion.",
                "InventoryController.Get" => "Product ID must not be empty.",
                "InventoryController.Movements" => "Invalid product ID or pagination bounds.",
                "StockReceiptsController.Receive" => "Invalid product ID, operation ID, positive quantity or reason.",
                "ReservationsController.Create" => "Invalid reservation ID or items; supply 1–50 distinct products with quantities of 1–10,000.",
                "ReservationsController.Get" => "Reservation ID must not be empty.",
                "ReservationsController.Release" => "Invalid reservation ID or missing/invalid release reason.",
                "OrdersController.Create" => "Invalid Idempotency-Key or items; supply 1–50 distinct products with quantities of 1–10,000.",
                "OrdersController.List" => "Invalid status filter or pagination bounds.",
                "OrdersController.Get" or "OrdersController.ReconcileCancellation" => "Order ID must not be empty.",
                "OrdersController.Cancel" => "Invalid order ID or missing/invalid cancellation reason.",
                _ => "Invalid request. See the validation errors for individual fields."
            };

        AddProblemResponse(operation, context, "500", "Unexpected server/database failure. ProblemDetails contains an errorCode and traceId; internal exception details are not exposed.");
        if (operation.RequestBody is not null)
        {
            AddProblemResponse(operation, context, "413", "REQUEST_TOO_LARGE: request body exceeds the 65,536-byte limit.");
            AddProblemResponse(operation, context, "415", "UNSUPPORTED_MEDIA_TYPE: send a JSON body with Content-Type: application/json.");
        }

        foreach (var (status, value) in operation.Responses)
        {
            if (value is not OpenApiResponse response) continue;
            if (response.Content is not { Count: > 0 }) continue;
            var media = response.Content.TryGetValue("application/json", out var json) ? json : response.Content.Values.First();
            var mediaType = int.TryParse(status, out var code) && code >= 400 ? "application/problem+json" : "application/json";
            response.Content = new Dictionary<string, OpenApiMediaType> { [mediaType] = media };
        }

        foreach (var value in operation.Parameters ?? [])
        {
            if (value is not OpenApiParameter parameter) continue;
            if (key == "OrdersController.List" && parameter.Name == "status")
            {
                parameter.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = [JsonValue.Create("Pending")!, JsonValue.Create("Confirmed")!, JsonValue.Create("Rejected")!,
                        JsonValue.Create("RequiresReview")!, JsonValue.Create("CancellationPending")!,
                        JsonValue.Create("Cancelled")!, JsonValue.Create("Fulfilled")!]
                };
                parameter.Description = "Optional saved order state. Leave unselected for all orders; select RequiresReview for manual investigation. Listing never reconciles orders or changes stock.";
            }
            parameter.Description = parameter.Name switch
            {
                "Idempotency-Key" => "Required stable key for one purchase: one nonblank header, at most 128 characters. Trimmed and case-sensitive. Keep the key and identical items when retrying or recovering Pending. Changed items return 409; a new purchase needs a new key. HTTP clients supply and preserve their own key.",
                "productId" when key == "ProductsController.SetStatus" => "Existing catalogue product ID. Empty GUID returns 400; unknown product returns 404. Malformed route GUIDs do not match this endpoint (404).",
                "productId" => "ID returned by ProductService.",
                "reservationId" => "Original reservation ID; for an order reservation this is its orderId. Use the same ID to inspect a rejected result.",
                "id" => key.StartsWith("Orders", StringComparison.Ordinal)
                    ? "System-generated orderId returned by order creation. GET does not trigger Pending recovery."
                    : "System-generated product ID returned by product creation.",
                "offset" => "Number of records to skip, starting at 0. Default 0.",
                "limit" => "Page size from 1 to 200. Default 100.",
                "search" => "Optional SKU/name substring, trimmed, at most 100 characters. Applied before pagination.",
                "isActive" => "Optional catalogue Active filter; this does not filter inventory availability.",
                "newestFirst" => "Set true for descending creation time and ID. Defaults to false for compatibility.",
                _ => parameter.Description
            };
        }
    }

    private static void AddProblemResponse(OpenApiOperation operation, OperationFilterContext context, string status, string description)
    {
        operation.Responses![status] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new()
                {
                    Schema = context.SchemaGenerator.GenerateSchema(typeof(ProblemDetails), context.SchemaRepository)
                }
            }
        };
    }

    private static void SetResponseExample(OpenApiOperation operation, string status, JsonNode example)
    {
        if (operation.Responses!.TryGetValue(status, out var value) && value is OpenApiResponse { Content: not null } response)
            foreach (var media in response.Content.Values)
                media.Example = example.DeepClone();
    }
}

public sealed class ApiSchemaDocumentation : ISchemaFilter
{
    private const string ReservationDetailsExample = """{"reservationId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","status":"Reserved","failureCode":null,"shortfalls":[],"items":[{"productId":"11111111-1111-1111-1111-111111111111","quantity":3},{"productId":"22222222-2222-2222-2222-222222222222","quantity":2}]}""";
    internal const string OrderResponseExample = """{"orderId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","status":"Confirmed","totalAmount":199.50,"lines":[{"productId":"11111111-1111-1111-1111-111111111111","sku":"MOUSE-01","name":"Wireless Mouse","unitPrice":59.90,"quantity":3,"lineTotal":179.70},{"productId":"22222222-2222-2222-2222-222222222222","sku":"PAD-01","name":"Mouse Pad","unitPrice":9.90,"quantity":2,"lineTotal":19.80}],"failureCode":null,"shortfalls":[],"createdAtUtc":"2026-10-22T00:00:00+00:00","updatedAtUtc":"2026-10-22T00:00:01+00:00","lastAttemptAtUtc":"2026-10-22T00:00:00+00:00","cancellationReason":null}""";

    internal static JsonNode ReservationExample(string status)
    {
        var example = JsonNode.Parse(ReservationDetailsExample)!;
        example["status"] = status;
        if (status == "Rejected")
        {
            example["failureCode"] = "INSUFFICIENT_STOCK";
            example["shortfalls"] = JsonNode.Parse("""[{"productId":"22222222-2222-2222-2222-222222222222","requested":2,"available":1}]""");
        }
        return example;
    }

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema mutable || mutable.Properties is null) return;
        var example = context.Type.Name switch
        {
            "ProductResponse" => """{"id":"11111111-1111-1111-1111-111111111111","sku":"MOUSE-01","name":"Wireless Mouse","description":"2.4 GHz","unitPrice":59.90,"isActive":true,"createdAtUtc":"2026-10-22T00:00:00+00:00","updatedAtUtc":"2026-10-22T00:00:00+00:00"}""",
            "ProductLookupResponse" => """{"products":[],"missingProductIds":["11111111-1111-1111-1111-111111111111"]}""",
            "InventoryResponse" => """{"productId":"11111111-1111-1111-1111-111111111111","onHand":10,"reserved":3,"availableQuantity":7,"version":"AAAAAAAAAAE="}""",
            "StockMovementResponse" => """{"id":2,"operationId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","productId":"11111111-1111-1111-1111-111111111111","movementType":"Reserve","onHandDelta":0,"reservedDelta":3,"onHandAfter":10,"reservedAfter":3,"reason":null,"createdAtUtc":"2026-10-22T00:00:00+00:00"}""",
            "ReceiveStockRequest" => """{"operationId":"cccccccc-cccc-cccc-cccc-cccccccccccc","quantity":5,"reason":"Supplier delivery"}""",
            "StockReceiptResponse" => """{"operationId":"cccccccc-cccc-cccc-cccc-cccccccccccc","productId":"11111111-1111-1111-1111-111111111111","quantity":5,"reason":"Supplier delivery","onHandAfter":15,"reservedAfter":3,"availableQuantityAfter":12,"createdAtUtc":"2026-10-22T00:00:00+00:00"}""",
            "ReservationResponse" => """{"reservationId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","status":"Reserved","failureCode":null,"shortfalls":[]}""",
            "ReservationDetailsResponse" => ReservationDetailsExample,
            "OrderResponse" => OrderResponseExample,
            "PendingOrderResponse" => """{"orderId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","status":"Pending","statusUrl":"/api/orders/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"}""",
            "CancelOrderRequest" or "ReleaseReservationRequest" => """{"reason":"Customer cancelled before shipment"}""",
            _ => null
        };
        if (example is not null) mutable.Example = JsonNode.Parse(example);
        foreach (var (name, value) in mutable.Properties)
        {
            if (value is not OpenApiSchema property) continue;
            property.Description = name switch
            {
                "reservationId" => "Stable, nonempty request ID supplied by the caller. OrderService uses the order ID. Preserve it on retries.",
                "operationId" => "ID of the operation. Receipt IDs come from the caller; stocktake IDs are system-generated. Reserve and Release share the order/reservation ID; MovementType separates them. A ledger row's id is a separate audit identifier.",
                "onHand" => "Total physical count, not a quantity to add. Must be nonnegative and at least Reserved.",
                "version" => "Opaque 8-byte SQL rowversion encoded as Base64. Changes on stock updates, including receipts, reservations and releases. This is not a timestamp.",
                "expectedVersion" => "Copy version from GET inventory for an existing stock count. Missing or stale versions return 409 STOCK_VERSION_CONFLICT. Null or omitted means initialise only; malformed tokens return 400. Refresh and review the count after a conflict, without automatic resubmission.",
                "reserved" => "Quantity held for confirmed orders or direct reservations; not a shipment count.",
                "availableQuantity" => "Current OnHand minus Reserved.",
                "onHandDelta" => "Signed change to physical stock made by this movement. Reserve movements use 0.",
                "reservedDelta" => "Signed change to held stock. Reserve adds the requested quantity; Release subtracts the original persisted quantity.",
                "onHandAfter" => "Physical stock immediately after this operation; later operations can change the current balance.",
                "reservedAfter" => "Reserved stock immediately after this operation.",
                "availableQuantityAfter" => "OnHandAfter minus ReservedAfter at the time of this receipt, including on replay.",
                "reason" => context.Type.Name is "CancelOrderRequest" or "ReleaseReservationRequest"
                    ? "Required, nonblank reason of at most 500 characters. First accepted cancellation reason is immutable; replay text is ignored."
                    : "Stocktake/receipt reason is trimmed and at most 300 characters. Release ledger reasons retain the accepted cancellation text, up to 500 characters.",
                "cancellationReason" => "First accepted cancellation reason; preserved on all retries, including different replay text. Null before cancellation.",
                "shortfalls" => "Original insufficient-stock details. Rejected replay does not recalculate availability after restock.",
                "missingProductIds" => "Requested IDs that do not exist. Inactive products are returned in products with isActive=false.",
                "items" => context.Type.Name == "ReservationDetailsResponse"
                    ? "Original persisted request lines, sorted by product ID, including rejected requests. Quantities remain unchanged after release or consumption and do not describe current stock balances."
                    : "1–50 non-null lines with distinct, nonempty product IDs. Each line quantity must be 1–10,000. Item order does not affect the request fingerprint.",
                "sku" => "Catalogue SKU is trimmed, uppercased and unique; at most 50 characters after normalisation. Order lines retain the saved SKU.",
                "name" => "Catalogue name is trimmed and at most 200 characters. Order lines retain the saved name.",
                "unitPrice" => "Nonnegative decimal with at most two fractional digits, up to 9999999999999999.99. Order lines retain the saved catalogue price.",
                "lineTotal" => "Decimal unit price multiplied by quantity, stored with two fractional digits.",
                "totalAmount" => "Sum of saved decimal line totals, within the decimal(18,2) storage range.",
                "isActive" => "Catalogue activation, independent of stock availability. Status updates require an explicit boolean; zero stock never changes it. Deactivation does not fence orders already validated across services.",
                _ => property.Description
            };
            if (name == "quantity")
                property.Description = context.Type.Name is "ReceiveStockRequest" or "StockReceiptResponse"
                    ? "Additional units received, from 1 to 2147483647, subject to the physical stock integer limit."
                    : "Whole units requested, from 1 to 10,000.";
        }
    }
}
