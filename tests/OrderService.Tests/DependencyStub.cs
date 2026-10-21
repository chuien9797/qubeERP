using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Shared.Contracts.Inventory;
using Shared.Contracts.Products;

namespace OrderService.Tests;

public sealed class DependencyStub : HttpMessageHandler
{
    private int productCalls;
    private int inventoryCalls;
    private int inventoryQueryCalls;
    public ConcurrentDictionary<Guid, ProductResponse> Products { get; } = new();
    public ConcurrentQueue<CreateReservationRequest> Reservations { get; } = new();
    public ConcurrentDictionary<Guid, ReservationResponse> Outcomes { get; } = new();
    public ConcurrentQueue<Guid> ReservationQueries { get; } = new();
    public int ProductCalls => Volatile.Read(ref productCalls);
    public int InventoryCalls => Volatile.Read(ref inventoryCalls);
    public int InventoryQueryCalls => Volatile.Read(ref inventoryQueryCalls);
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ProductResponse { get; set; }
    public Func<CreateReservationRequest, CancellationToken, Task<HttpResponseMessage>>? InventoryResponse { get; set; }
    public Func<Guid, CancellationToken, Task<HttpResponseMessage>>? InventoryQueryResponse { get; set; }

    public Guid AddProduct(decimal price = 12.50m, bool active = true)
    {
        var id = Guid.NewGuid();
        Products[id] = new ProductResponse(id, "SKU-" + id.ToString("N"), "Original product", null,
            price, active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return id;
    }

    public async Task<HttpResponseMessage> LookupAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var lookup = (await request.Content!.ReadFromJsonAsync<ProductLookupRequest>(cancellationToken))!;
        return Json(HttpStatusCode.OK, new ProductLookupResponse(
            lookup.ProductIds!.Where(Products.ContainsKey).Select(id => Products[id]).ToArray(),
            lookup.ProductIds!.Where(id => !Products.ContainsKey(id)).ToArray()));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/api/products/lookup")
        {
            Interlocked.Increment(ref productCalls);
            return await (ProductResponse?.Invoke(request, cancellationToken) ?? LookupAsync(request, cancellationToken));
        }

        if (request.RequestUri.AbsolutePath == "/api/inventory/reservations" && request.Method == HttpMethod.Post)
        {
            Interlocked.Increment(ref inventoryCalls);
            var reservation = (await request.Content!.ReadFromJsonAsync<CreateReservationRequest>(cancellationToken))!;
            Reservations.Enqueue(reservation);
            if (InventoryResponse is not null)
            {
                return await InventoryResponse(reservation, cancellationToken);
            }

            var candidate = new ReservationResponse(reservation.ReservationId!.Value, "Reserved", null, []);
            var saved = Outcomes.GetOrAdd(candidate.ReservationId, candidate);
            return Json(ReferenceEquals(saved, candidate) ? HttpStatusCode.Created : HttpStatusCode.OK, saved);
        }

        if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.StartsWith("/api/inventory/reservations/", StringComparison.Ordinal))
        {
            var id = Guid.Parse(request.RequestUri.Segments[^1]);
            Interlocked.Increment(ref inventoryQueryCalls);
            ReservationQueries.Enqueue(id);
            if (InventoryQueryResponse is not null)
            {
                return await InventoryQueryResponse(id, cancellationToken);
            }

            return Outcomes.TryGetValue(id, out var saved) ? Json(HttpStatusCode.OK, saved)
                : Json(HttpStatusCode.NotFound, new { errorCode = "RESERVATION_NOT_FOUND", reservationId = id });
        }

        throw new InvalidOperationException("Unexpected dependency call: " + request.RequestUri);
    }

    public static HttpResponseMessage Json<T>(HttpStatusCode status, T body) => new(status) { Content = JsonContent.Create(body) };

    protected override void Dispose(bool disposing) { }
}
