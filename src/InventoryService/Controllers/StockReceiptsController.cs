using InventoryService.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Contracts.Inventory;

namespace InventoryService.Controllers;

[ApiController]
[Route("api/inventory/{productId:guid}/receipts")]
[RequestSizeLimit(65536)]
public sealed class StockReceiptsController(StockReceiptService receipts) : ControllerBase
{
    [HttpPost]
    [Tags("Inventory")]
    [EndpointSummary("Add received stock to a product.")]
    [EndpointDescription("Choose a product with initialised inventory and enter the additional quantity received. Existing stock is increased; Reserved stays unchanged. Swagger generates the operation ID. Keep that ID and the same quantity and reason when retrying; a successful receipt is applied once. Choose New receipt for a separate delivery. The response contains the original balances after this receipt, even on a later retry. Use PUT to initialise inventory or correct the total physical count.")]
    [ProducesResponseType<StockReceiptResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<StockReceiptResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<StockReceiptResponse>> Receive(Guid productId, ReceiveStockRequest request,
        CancellationToken cancellationToken)
    {
        if (productId == Guid.Empty)
            return ReceiptProblem(400, "Product ID must not be empty.", "INVALID_REQUEST");

        StockReceiptResult result;
        try
        {
            result = await receipts.ReceiveAsync(productId, request, cancellationToken);
        }
        catch (RetryLimitExceededException)
        {
            return ReceiptProblem(503, "Receipt outcome could not be confirmed. Retry with the same operation ID and payload.", "RECEIPT_UNAVAILABLE");
        }
        return result.Status switch
        {
            StockReceiptStatus.Created => StatusCode(201, result.Receipt),
            StockReceiptStatus.Replayed => Ok(result.Receipt),
            StockReceiptStatus.Conflict => ReceiptProblem(409, "Operation ID was already used for a different receipt.", "RECEIPT_PAYLOAD_CONFLICT"),
            StockReceiptStatus.StockNotFound => ReceiptProblem(404, "Initialise this product's stock with PUT before receiving stock.", "STOCK_NOT_FOUND"),
            StockReceiptStatus.CapacityExceeded => ReceiptProblem(409, "The receipt would exceed the maximum stock quantity.", "STOCK_CAPACITY_EXCEEDED"),
            _ => throw new InvalidOperationException("Unexpected stock receipt outcome.")
        };
    }

    private ObjectResult ReceiptProblem(int status, string title, string code)
    {
        var response = Problem(statusCode: status, title: title);
        ((ProblemDetails)response.Value!).Extensions["errorCode"] = code;
        return response;
    }
}
