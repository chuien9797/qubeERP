using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using OrderService.Application;
using Shared.Contracts.Orders;

namespace OrderService.Controllers;

[ApiController]
[Route("api/orders")]
[Tags("Orders")]
[RequestSizeLimit(65536)]
public sealed class OrdersController(OrderApplicationService orders) : ControllerBase
{
    [HttpPost]
    [EndpointSummary("Create an order for selected products.")]
    [EndpointDescription("Supply 1–50 distinct active products with quantities of 1–10,000 and an Idempotency-Key header. Preserve the key and items to retry the same order or recover Pending. Use a new key for a separate purchase. OrderService generates the order ID and saves catalogue names, SKUs and prices. Confirmation reserves stock without reducing physical OnHand. A shortage rejects the whole order. A 202 response means the outcome is uncertain; retry the same key and items.")]
    [ProducesResponseType<OrderResponse>(201)]
    [ProducesResponseType<OrderResponse>(200)]
    [ProducesResponseType<PendingOrderResponse>(202)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(413)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(502)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<ActionResult<OrderResponse>> Create(CreateOrderRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(128)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var keys = Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 128)
        {
            return OrderProblem(400, "Provide one nonempty Idempotency-Key of at most 128 characters.", "INVALID_IDEMPOTENCY_KEY");
        }

        var result = await orders.CreateAsync(idempotencyKey!.Trim(), request, cancellationToken);
        if (result.ErrorCode is not null)
        {
            return result.ErrorCode switch
            {
                "PRODUCT_UNAVAILABLE" => OrderProblem(503, "Product lookup is unavailable.", result.ErrorCode),
                "INVALID_PRODUCT" => OrderProblem(422, "Some products are missing or inactive.", result.ErrorCode, invalidIds: result.InvalidProductIds),
                "ORDER_TOTAL_OUT_OF_RANGE" => OrderProblem(422, "Order amounts exceed the supported decimal range.", result.ErrorCode),
                "IDEMPOTENCY_KEY_CONFLICT" => OrderProblem(409, "This key was already used for different items.", result.ErrorCode, result.Order),
                _ => throw new InvalidOperationException("Unexpected order validation result.")
            };
        }

        var order = result.Order!;
        var statusUrl = Url.Action(nameof(Get), new { id = order.OrderId })!;
        if (result.IsNew)
        {
            Response.Headers.Location = statusUrl;
        }

        return order.Status switch
        {
            "Confirmed" when result.IsNew => CreatedAtAction(nameof(Get), new { id = order.OrderId }, order),
            "Confirmed" => Ok(order),
            "Cancelled" or "Fulfilled" => Ok(order),
            "CancellationPending" => Accepted(statusUrl, new PendingOrderResponse(order.OrderId, order.Status, statusUrl)),
            "Pending" => Accepted(statusUrl, new PendingOrderResponse(order.OrderId, order.Status, statusUrl)),
            "Rejected" when order.FailureCode == "INSUFFICIENT_STOCK" =>
                OrderProblem(409, "Insufficient stock.", order.FailureCode, order),
            "Rejected" or "RequiresReview" => OrderProblem(502, "The Inventory response needs investigation.", order.FailureCode!, order),
            _ => throw new InvalidOperationException("Unexpected order status.")
        };
    }

    [HttpPost("{id:guid}/cancel")]
    [EndpointSummary("Cancel a confirmed order and release its reserved stock.")]
    [EndpointDescription("Requires a reason of 1–500 characters. The first accepted reason is immutable. Replays use the same reservation identity and saved reason. 202 means release is uncertain; retry cancel or reconcile cancellation. Pending orders must reconcile their initial reservation first. Inventory contradictions require manual review.")]
    [ProducesResponseType<OrderResponse>(200)]
    [ProducesResponseType<PendingOrderResponse>(202)]
    [ProducesResponseType<ValidationProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<ActionResult<OrderResponse>> Cancel(Guid id, CancelOrderRequest request, CancellationToken cancellationToken) =>
        CancellationResponse(await orders.CancelAsync(id, request.Reason!, cancellationToken));

    [HttpPost("{id:guid}/reconcile-cancellation")]
    [EndpointSummary("Recover an uncertain cancellation using the original reservation.")]
    [EndpointDescription("Queries Inventory first. Verified Released completes cancellation; Reserved re-drives release with the saved reason and original ID. Timeouts remain CancellationPending. RequiresReview never transitions automatically. Initial Pending reservation recovery remains the original order POST.")]
    [ProducesResponseType<OrderResponse>(200)]
    [ProducesResponseType<PendingOrderResponse>(202)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<ActionResult<OrderResponse>> ReconcileCancellation(Guid id, CancellationToken cancellationToken) =>
        CancellationResponse(await orders.ReconcileCancellationAsync(id, cancellationToken));

    private ActionResult<OrderResponse> CancellationResponse(OrderResult result)
    {
        if (result.Order is null) return OrderProblem(404, "Order not found.", "ORDER_NOT_FOUND");
        var order = result.Order;
        if (order.Status == "Cancelled") return Ok(order);
        if (order.Status == "CancellationPending")
        {
            var url = Url.Action(nameof(Get), new { id = order.OrderId })!;
            return Accepted(url, new PendingOrderResponse(order.OrderId, order.Status, url));
        }
        return OrderProblem(409, order.Status == "Pending"
            ? "Reconcile the reservation first."
            : order.Status == "RequiresReview" ? "Cancellation requires manual review." : "Only a Confirmed order may start cancellation.",
            result.ErrorCode ?? "ORDER_NOT_CANCELLABLE", order);
    }

    [HttpGet]
    [EndpointSummary("List saved orders, optionally filtered by status.")]
    [EndpointDescription("Use status=RequiresReview for manual investigation. Optional newestFirst=true returns recent orders first; the default remains oldest first. Filtering and sorting precede pagination. This read never reconciles orders or changes stock.")]
    [ProducesResponseType<IReadOnlyList<OrderResponse>>(200)]
    [ProducesResponseType<ValidationProblemDetails>(400)]
    public async Task<ActionResult<IReadOnlyList<OrderResponse>>> List(
        [FromQuery, RegularExpression("^(Pending|Confirmed|Rejected|RequiresReview|CancellationPending|Cancelled|Fulfilled)$")] string? status,
        CancellationToken cancellationToken, [FromQuery, Range(0, int.MaxValue)] int offset = 0,
        [FromQuery, Range(1, 200)] int limit = 100, [FromQuery] bool newestFirst = false) =>
        Ok(await orders.ListAsync(status, offset, limit, cancellationToken, newestFirst));

    [HttpGet("{id:guid}")]
    [EndpointSummary("View an order and its saved prices.")]
    [EndpointDescription("Retrieve a stored order by ID. Reading an order does not retry reservations or change its status. To recover a Pending order, repeat its original POST with the same request key and items.")]
    [ProducesResponseType<OrderResponse>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<OrderResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(id, cancellationToken);
        return order is null ? OrderProblem(404, "Order not found.", "ORDER_NOT_FOUND") : Ok(order);
    }

    private ObjectResult OrderProblem(int status, string title, string code, OrderResponse? order = null,
        IReadOnlyList<Guid>? invalidIds = null)
    {
        var result = Problem(statusCode: status, title: title);
        var problem = (ProblemDetails)result.Value!;
        problem.Extensions["errorCode"] = code;
        if (order is not null)
        {
            problem.Extensions["orderId"] = order.OrderId;
            problem.Extensions["orderStatus"] = order.Status;
            problem.Extensions["statusUrl"] = Url.Action(nameof(Get), new { id = order.OrderId });
            if (order.FailureCode == "INSUFFICIENT_STOCK")
            {
                problem.Extensions["shortfalls"] = order.Shortfalls;
            }
        }

        if (invalidIds is not null)
        {
            problem.Extensions["invalidProductIds"] = invalidIds;
        }

        return result;
    }
}
