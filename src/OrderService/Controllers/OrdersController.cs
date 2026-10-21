using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using OrderService.Application;
using Shared.Contracts.Orders;

namespace OrderService.Controllers;

[ApiController]
[Route("api/orders")]
[RequestSizeLimit(65536)]
public sealed class OrdersController(OrderApplicationService orders) : ControllerBase
{
    [HttpPost]
    [EndpointSummary("Create an order with saved product prices and an atomic stock reservation.")]
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
            "Pending" => Accepted(statusUrl, new PendingOrderResponse(order.OrderId, order.Status, statusUrl)),
            "Rejected" when order.FailureCode == "INSUFFICIENT_STOCK" =>
                OrderProblem(409, "Insufficient stock.", order.FailureCode, order),
            "Rejected" or "RequiresReview" => OrderProblem(502, "The Inventory response needs investigation.", order.FailureCode!, order),
            _ => throw new InvalidOperationException("Unexpected order status.")
        };
    }

    [HttpGet("{id:guid}")]
    [EndpointSummary("Read an order and its original prices without contacting other services.")]
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
            problem.Extensions["status"] = order.Status;
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
