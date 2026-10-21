using System.ComponentModel.DataAnnotations;
using InventoryService.Application;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts.Inventory;

namespace InventoryService.Controllers;

[ApiController]
[Route("api/inventory")]
[RequestSizeLimit(65536)]
public sealed class InventoryController(StockService stock, ILogger<InventoryController> logger) : ControllerBase
{
    [HttpPut("{productId:guid}")]
    [EndpointSummary("Set a product's physical stock count.")]
    [EndpointDescription("Choose a product, enter its total physical quantity and give a reason. This replaces the previous count; it does not add a quantity. Reserved stock stays unchanged, so the new count cannot be lower than Reserved. A product must exist before its first stock count is created.")]
    [ProducesResponseType<InventoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
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

        var result = await stock.SetAsync(productId, request.OnHand!.Value, request.Reason!, cancellationToken);
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
            case StockUpdateStatus.StockNotFound:
                return InventoryProblem(404, "Stock item not found.", "STOCK_NOT_FOUND");
            default:
                throw new InvalidOperationException("Unexpected stock update outcome.");
        }
    }

    [HttpGet("{productId:guid}")]
    [EndpointSummary("Check a product's stock balance.")]
    [EndpointDescription("Choose a product to see OnHand, Reserved and AvailableQuantity. AvailableQuantity equals OnHand minus Reserved. A product without an initial stock count returns 404.")]
    [ProducesResponseType<InventoryResponse>(StatusCodes.Status200OK)]
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
    [EndpointDescription("Choose a product to see its stock-count changes and reservations, oldest first. Reservations increase Reserved and leave physical OnHand unchanged. Use offset and limit to page through the results.")]
    [ProducesResponseType<IReadOnlyList<StockMovementResponse>>(StatusCodes.Status200OK)]
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
