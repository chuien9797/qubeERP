using System.Net;
using System.Text.Json;
using Shared.Contracts.Inventory;

namespace OrderService.Clients;

public sealed class InventoryClient(HttpClient httpClient, ILogger<InventoryClient> logger) : IInventoryClient
{
    public async Task<InventoryResult> ReserveAsync(CreateReservationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync("/api/inventory/reservations", request, cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                return new InventoryResult(InventoryOutcome.Uncertain);
            }

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                var reservation = await response.Content.ReadFromJsonAsync<ReservationResponse>(cancellationToken);
                return reservation is not null && reservation.ReservationId == request.ReservationId && reservation.Status == "Reserved"
                    && reservation.FailureCode is null && reservation.Shortfalls is { Count: 0 }
                    ? new InventoryResult(InventoryOutcome.Reserved)
                    : new InventoryResult(InventoryOutcome.UnexpectedResponse);
            }

            var problem = await response.Content.ReadFromJsonAsync<InventoryProblem>(cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest && problem?.ErrorCode == "INVALID_RESERVATION")
            {
                return new InventoryResult(InventoryOutcome.InvalidRequest);
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                if (problem?.ErrorCode == "RESERVATION_PAYLOAD_CONFLICT")
                {
                    return new InventoryResult(InventoryOutcome.PayloadConflict);
                }

                if (problem?.ErrorCode == "INSUFFICIENT_STOCK" && problem.ReservationId == request.ReservationId
                    && ValidShortfalls(problem.Shortfalls, request.Items!))
                {
                    return new InventoryResult(InventoryOutcome.Rejected, problem.Shortfalls);
                }
            }

            return new InventoryResult(InventoryOutcome.UnexpectedResponse);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Inventory reservation call failed for {OrderId}", request.ReservationId);
            return new InventoryResult(InventoryOutcome.Uncertain);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Inventory reservation call timed out for {OrderId}", request.ReservationId);
            return new InventoryResult(InventoryOutcome.Uncertain);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Inventory returned invalid JSON for {OrderId}", request.ReservationId);
            return new InventoryResult(InventoryOutcome.UnexpectedResponse);
        }
    }

    private static bool ValidShortfalls(StockShortfall[]? shortfalls, ReservationItemRequest[] items)
    {
        return shortfalls is { Length: > 0 } && shortfalls.All(shortfall => shortfall is not null
            && shortfall.Available >= 0 && shortfall.Available < shortfall.Requested
            && items.Any(item => item.ProductId == shortfall.ProductId && item.Quantity == shortfall.Requested))
            && shortfalls.Select(shortfall => shortfall.ProductId).Distinct().Count() == shortfalls.Length;
    }

    private sealed record InventoryProblem(string? ErrorCode, Guid? ReservationId, StockShortfall[]? Shortfalls);
}
