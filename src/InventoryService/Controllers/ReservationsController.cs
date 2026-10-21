using InventoryService.Application;
using Microsoft.AspNetCore.Mvc;
using Shared.Contracts.Inventory;

namespace InventoryService.Controllers;

[ApiController]
[Route("api/inventory/reservations")]
[RequestSizeLimit(65536)]
public sealed class ReservationsController(ReservationService reservations) : ControllerBase
{
    [HttpPost]
    [EndpointSummary("Reserve every requested item in one transaction.")]
    [ProducesResponseType<ReservationResponse>(StatusCodes.Status201Created)]
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
                return CreatedAtAction(nameof(Get), new { reservationId = id }, result.Reservation);
            case ReservationOutcome.Rejected:
                return ReservationProblem(409, "Insufficient stock.", "INSUFFICIENT_STOCK", id, result.Reservation!.Shortfalls);
            case ReservationOutcome.Unavailable:
                return ReservationProblem(503, "Reservation could not be completed. Retry with the same ID.", "RESERVATION_UNAVAILABLE", id);
            default:
                throw new InvalidOperationException("Unexpected reservation outcome.");
        }
    }

    [HttpGet("{reservationId:guid}")]
    [EndpointSummary("Read a persisted reservation outcome without changing stock.")]
    [ProducesResponseType<ReservationResponse>(StatusCodes.Status200OK)]
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
