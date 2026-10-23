using System.ComponentModel.DataAnnotations;
using InventoryService.Application;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts.Inventory;

namespace InventoryService.Controllers;

[ApiController]
[Route("api/inventory")]
[Tags("Inventory")]
[RequestSizeLimit(65536)]
public sealed class InventoryController(StockService stock, ILogger<InventoryController> logger) : ControllerBase
{
    [HttpPut("{productId:guid}")]
    [EndpointSummary("Set a product's physical stock count.")]
    [EndpointDescription("Supply the product ID, total physical quantity and reason. For existing stock, send expectedVersion from the last GET inventory response. A stale or omitted version returns 409 STOCK_VERSION_CONFLICT without changing stock or movements; reload and review the count before a new submission. Omit expectedVersion or send null only to initialise absent stock. Concurrent opening counts never overwrite the winning count. This replaces the previous count; it does not add a quantity. Use POST receipts to add newly received stock instead. Reserved stock stays unchanged, so the new count cannot be lower than Reserved. A product must exist before its first stock count is created. Absolute writes are not automatically retried after uncertain database errors. If the response is lost, inspect stock and movements before making a separate count; never assume failure or blindly retry.")]
    [ProducesResponseType<InventoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<InventoryResponse>> Set(
        Guid productId, SetStockRequest request, CancellationToken cancellationToken)
    {
        if (productId == Guid.Empty)
        {
            return InventoryProblem(400, "Product ID must not be empty.", "INVALID_REQUEST");
        }

        var result = await stock.SetAsync(productId, request.OnHand!.Value, request.Reason!, request.ExpectedVersion, cancellationToken);
        switch (result.Status)
        {
            case StockUpdateStatus.Updated:
                logger.LogInformation("Set stock for {ProductId} to {OnHand}", productId, request.OnHand);
                return Ok(result.Stock);
            case StockUpdateStatus.ProductNotFound:
                return InventoryProblem(422, "Product not found.", "PRODUCT_NOT_FOUND");
            case StockUpdateStatus.ProductUnavailable:
                return InventoryProblem(503, "Product service is unavailable.", "PRODUCT_UNAVAILABLE");
            case StockUpdateStatus.BelowReserved:
                return InventoryProblem(409, "On-hand quantity cannot be lower than reserved stock.", "STOCK_BELOW_RESERVED");
            case StockUpdateStatus.VersionConflict:
                return InventoryProblem(409, "Stock changed. Reload inventory and review the physical count before saving again.", "STOCK_VERSION_CONFLICT");
            case StockUpdateStatus.StockNotFound:
                return InventoryProblem(404, "Stock item not found.", "STOCK_NOT_FOUND");
            default:
                throw new InvalidOperationException("Unexpected stock update outcome.");
        }
    }

    [HttpGet("{productId:guid}")]
    [EndpointSummary("Check a product's stock balance.")]
    [EndpointDescription("Retrieve OnHand, Reserved, AvailableQuantity and an opaque Base64 version. Use that version as expectedVersion for a subsequent physical count. Receipts, reservations, releases and changed physical counts invalidate previous versions. AvailableQuantity equals OnHand minus Reserved. A product without an initial stock count returns 404.")]
    [ProducesResponseType<InventoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InventoryResponse>> Get(Guid productId, CancellationToken cancellationToken)
    {
        if (productId == Guid.Empty)
        {
            return InventoryProblem(400, "Product ID must not be empty.", "INVALID_REQUEST");
        }

        var balance = await stock.GetAsync(productId, cancellationToken);
        if (balance is null)
        {
            return InventoryProblem(404, "Stock item not found.", "STOCK_NOT_FOUND");
        }

        return Ok(balance);
    }

    [HttpGet("{productId:guid}/movements")]
    [EndpointSummary("View a product's stock movements.")]
    [EndpointDescription("Retrieve opening stock, stocktake corrections, receipts, reservations and releases, oldest first by movement ID. Reservations increase Reserved; releases decrease Reserved. Both leave physical OnHand unchanged. Movement IDs identify audit rows, not orders. Reserve and Release use the original order/reservation ID as operationId. The After fields are historical balances immediately after that operation. This endpoint is read-only. Use offset and limit to page through the results.")]
    [ProducesResponseType<IReadOnlyList<StockMovementResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<StockMovementResponse>>> Movements(
        Guid productId, CancellationToken cancellationToken,
        [FromQuery, Range(0, int.MaxValue)] int offset = 0,
        [FromQuery, Range(1, 200)] int limit = 100)
    {
        if (productId == Guid.Empty)
        {
            return InventoryProblem(400, "Product ID must not be empty.", "INVALID_REQUEST");
        }

        if (await stock.GetAsync(productId, cancellationToken) is null)
        {
            return InventoryProblem(404, "Stock item not found.", "STOCK_NOT_FOUND");
        }

        var movements = await stock.GetMovementsAsync(productId, offset, limit, cancellationToken);
        return Ok(movements);
    }

    private ObjectResult InventoryProblem(int status, string title, string errorCode)
    {
        var response = Problem(statusCode: status, title: title);
        var problem = (ProblemDetails)response.Value!;
        problem.Extensions["errorCode"] = errorCode;
        return response;
    }
}
