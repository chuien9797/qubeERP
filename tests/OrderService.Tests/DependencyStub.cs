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
    public ConcurrentDictionary<Guid, ProductResponse> Products { get; } = new();
    public ConcurrentQueue<CreateReservationRequest> Reservations { get; } = new();
    public int ProductCalls => Volatile.Read(ref productCalls);
    public int InventoryCalls => Volatile.Read(ref inventoryCalls);
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ProductResponse { get; set; }
    public Func<CreateReservationRequest, CancellationToken, Task<HttpResponseMessage>>? InventoryResponse { get; set; }

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
            return InventoryResponse is null
                ? Json(HttpStatusCode.Created, new ReservationResponse(reservation.ReservationId!.Value, "Reserved", null, []))
                : await InventoryResponse(reservation, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected dependency call: " + request.RequestUri);
    }

    public static HttpResponseMessage Json<T>(HttpStatusCode status, T body) => new(status) { Content = JsonContent.Create(body) };

    protected override void Dispose(bool disposing) { }
}
