using InventoryService.Application;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts.Inventory;

namespace InventoryService.Controllers;

[ApiController]
[Route("api/inventory/reservations")]
[Tags("Reservations")]
[RequestSizeLimit(65536)]
public sealed class ReservationsController(ReservationService reservations) : ControllerBase
{
    [HttpPost]
    [EndpointSummary("Reserve stock for selected products.")]
    [EndpointDescription("In Swagger, select products and quantities; the reservation ID is generated for you. Execute again with the same ID to retry safely. Use New reservation for a separate request. If any product has insufficient stock, nothing is reserved and that request keeps its rejected result. OrderService normally calls this endpoint using the order ID.")]
    [ProducesResponseType<ReservationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ReservationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ReservationResponse>> Create(CreateReservationRequest request, CancellationToken cancellationToken)
    {
        var id = request.ReservationId!.Value;
        var result = await reservations.ReserveAsync(request, cancellationToken);
        switch (result.Outcome)
        {
            case ReservationOutcome.Reserved:
                if (result.IsReplay)
                {
                    return Ok(result.Reservation);
                }

                return CreatedAtAction(nameof(Get), new { reservationId = id }, result.Reservation);
            case ReservationOutcome.Rejected:
                if (!result.IsReplay)
                {
                    Response.Headers.Location = Url.Action(nameof(Get), new { reservationId = id });
                }

                return ReservationProblem(409, "Insufficient stock.", "INSUFFICIENT_STOCK", id, result.Reservation!.Shortfalls);
            case ReservationOutcome.PayloadConflict:
                return ReservationProblem(409, "Reservation ID was already used with different items.", "RESERVATION_PAYLOAD_CONFLICT", id);
            case ReservationOutcome.Unavailable:
                return ReservationProblem(503, "Reservation could not be completed. Retry with the same ID.", "RESERVATION_UNAVAILABLE", id);
            default:
                throw new InvalidOperationException("Unexpected reservation outcome.");
        }
    }

    [HttpPost("{reservationId:guid}/release")]
    [EndpointSummary("Release all stock held by a reservation.")]
    [EndpointDescription("Uses the original persisted lines and reservation ID. Released replays return 200 without another movement; Rejected or Consumed returns 409. OnHand is unchanged. The first committed release reason is retained in the ledger.")]
    [ProducesResponseType<ReservationResponse>(200)]
    [ProducesResponseType<ValidationProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<ActionResult<ReservationResponse>> Release(Guid reservationId, ReleaseReservationRequest request,
        CancellationToken cancellationToken)
    {
        if (reservationId == Guid.Empty)
            return ReservationProblem(400, "Reservation ID must not be empty.", "INVALID_RESERVATION", reservationId);
        var result = await reservations.ReleaseAsync(reservationId, request.Reason!, cancellationToken);
        return result.Outcome switch
        {
            ReleaseOutcome.Released => Ok(result.Reservation),
            ReleaseOutcome.NotFound => ReservationProblem(404, "Reservation not found.", "RESERVATION_NOT_FOUND", reservationId),
            ReleaseOutcome.Conflict => ReservationProblem(409, "Only a Reserved reservation may be released.", "RESERVATION_NOT_RELEASABLE", reservationId),
            _ => ReservationProblem(503, "Release outcome is uncertain. Retry the same reservation ID.", "RELEASE_UNAVAILABLE", reservationId)
        };
    }

    [HttpGet("{reservationId:guid}")]
    [EndpointSummary("Check a reservation's result.")]
    [EndpointDescription("Choose a recent reservation in Swagger or enter a known reservation ID. For an order reservation, use its order ID. Reading a reservation does not change stock.")]
    [ProducesResponseType<ReservationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReservationResponse>> Get(Guid reservationId, CancellationToken cancellationToken)
    {
        if (reservationId == Guid.Empty)
        {
            return ReservationProblem(400, "Reservation ID must not be empty.", "INVALID_RESERVATION", reservationId);
        }

        var reservation = await reservations.GetAsync(reservationId, cancellationToken);
        if (reservation is null)
        {
            return ReservationProblem(404, "Reservation not found.", "RESERVATION_NOT_FOUND", reservationId);
        }

        return Ok(reservation);
    }

    private ObjectResult ReservationProblem(int status, string title, string errorCode, Guid id,
        IReadOnlyList<StockShortfall>? shortfalls = null)
    {
        var response = Problem(statusCode: status, title: title);
        var problem = (ProblemDetails)response.Value!;
        problem.Extensions["errorCode"] = errorCode;
        problem.Extensions["reservationId"] = id;
        if (shortfalls is not null)
        {
            problem.Extensions["shortfalls"] = shortfalls;
        }

        return response;
    }
}
