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
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                logger.LogWarning("Inventory reservation returned HTTP {StatusCode} for {OrderId}; outcome is uncertain",
                    (int)response.StatusCode, request.ReservationId);
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

    public async Task<InventoryResult> GetAsync(Guid reservationId, IReadOnlyList<ReservationItemRequest> items,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync($"/api/inventory/reservations/{reservationId}", cancellationToken);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                logger.LogWarning("Inventory reservation query returned HTTP {StatusCode} for {OrderId}; outcome is uncertain",
                    (int)response.StatusCode, reservationId);
                return new InventoryResult(InventoryOutcome.Uncertain);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var problem = await response.Content.ReadFromJsonAsync<InventoryProblem>(cancellationToken);
                return problem?.ErrorCode == "RESERVATION_NOT_FOUND" && problem.ReservationId == reservationId
                    ? new InventoryResult(InventoryOutcome.NotFound)
                    : new InventoryResult(InventoryOutcome.UnexpectedResponse);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new InventoryResult(InventoryOutcome.UnexpectedResponse);
            }

            var reservation = await response.Content.ReadFromJsonAsync<ReservationDetailsResponse>(cancellationToken);
            if (reservation is null || reservation.ReservationId != reservationId || !MatchingItems(reservation.Items, items))
            {
                return new InventoryResult(InventoryOutcome.UnexpectedResponse);
            }

            if (reservation.Status == "Reserved" && reservation.FailureCode is null && reservation.Shortfalls is { Count: 0 })
            {
                return new InventoryResult(InventoryOutcome.Reserved);
            }

            if (reservation.Status == "Rejected" && reservation.FailureCode == "INSUFFICIENT_STOCK"
                && ValidShortfalls(reservation.Shortfalls, items))
            {
                return new InventoryResult(InventoryOutcome.Rejected, reservation.Shortfalls);
            }

            return new InventoryResult(InventoryOutcome.UnexpectedResponse);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Inventory reservation query failed for {OrderId}", reservationId);
            return new InventoryResult(InventoryOutcome.Uncertain);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Inventory reservation query timed out for {OrderId}", reservationId);
            return new InventoryResult(InventoryOutcome.Uncertain);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Inventory reservation query returned invalid JSON for {OrderId}", reservationId);
            return new InventoryResult(InventoryOutcome.UnexpectedResponse);
        }
    }

    private static bool MatchingItems(IReadOnlyList<ReservationItemResponse>? actual, IReadOnlyList<ReservationItemRequest> expected) =>
        actual is not null && actual.Count == expected.Count
        && actual.All(line => line is not null && expected.Any(item => item.ProductId == line.ProductId && item.Quantity == line.Quantity))
        && actual.Select(line => line.ProductId).Distinct().Count() == actual.Count;

    private static bool ValidShortfalls(IReadOnlyList<StockShortfall>? shortfalls, IReadOnlyList<ReservationItemRequest> items)
    {
        return shortfalls is { Count: > 0 } && shortfalls.All(shortfall => shortfall is not null
            && shortfall.Available >= 0 && shortfall.Available < shortfall.Requested
            && items.Any(item => item.ProductId == shortfall.ProductId && item.Quantity == shortfall.Requested))
            && shortfalls.Select(shortfall => shortfall.ProductId).Distinct().Count() == shortfalls.Count;
    }

    public Task<InventoryResult> ReleaseAsync(Guid reservationId, string reason, CancellationToken cancellationToken) =>
        SendReleaseAsync(reservationId, reason, cancellationToken);

    public Task<InventoryResult> GetReleaseStatusAsync(Guid reservationId, CancellationToken cancellationToken) =>
        SendReleaseAsync(reservationId, null, cancellationToken);

    private async Task<InventoryResult> SendReleaseAsync(Guid id, string? reason, CancellationToken cancellationToken)
    {
        try
        {
            using var response = reason is null
                ? await httpClient.GetAsync($"/api/inventory/reservations/{id}", cancellationToken)
                : await httpClient.PostAsJsonAsync($"/api/inventory/reservations/{id}/release",
                    new ReleaseReservationRequest { Reason = reason }, cancellationToken);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                return new InventoryResult(InventoryOutcome.Uncertain);
            if (response.StatusCode == HttpStatusCode.Conflict)
                return new InventoryResult(InventoryOutcome.ReleaseConflict);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new InventoryResult(InventoryOutcome.NotFound);
            if (response.StatusCode != HttpStatusCode.OK)
                return new InventoryResult(InventoryOutcome.UnexpectedResponse);
            var reservation = await response.Content.ReadFromJsonAsync<ReservationResponse>(cancellationToken);
            if (reservation is null || reservation.ReservationId != id || reservation.FailureCode is not null
                || reservation.Shortfalls is not { Count: 0 })
                return new InventoryResult(InventoryOutcome.UnexpectedResponse);
            return new InventoryResult(reservation.Status switch
            {
                "Released" => InventoryOutcome.Released,
                "Reserved" when reason is null => InventoryOutcome.Reserved,
                "Consumed" => InventoryOutcome.ReleaseConflict,
                _ => InventoryOutcome.UnexpectedResponse
            });
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Inventory release request failed for {OrderId}", id);
            return new InventoryResult(InventoryOutcome.Uncertain);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new InventoryResult(InventoryOutcome.Uncertain);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Inventory release returned invalid JSON for {OrderId}", id);
            return new InventoryResult(InventoryOutcome.UnexpectedResponse);
        }
    }

    private sealed record InventoryProblem(string? ErrorCode, Guid? ReservationId, StockShortfall[]? Shortfalls);
}
