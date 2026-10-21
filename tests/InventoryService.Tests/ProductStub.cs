using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Shared.Contracts.Products;

namespace InventoryService.Tests;

public sealed class ProductStub : HttpMessageHandler
{
    private readonly ConcurrentDictionary<Guid, Func<HttpResponseMessage>> responses = new();
    private readonly ConcurrentDictionary<Guid, int> calls = new();
    private readonly ConcurrentDictionary<Guid, LookupGate> gates = new();

    public void AddProduct(Guid id, bool active = true)
    {
        responses[id] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ProductResponse(id, "TEST-SKU", "Test product", null,
                10m, active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
        };
    }

    public void Respond(Guid id, HttpStatusCode status, string body = "")
    {
        responses[id] = () => new HttpResponseMessage(status) { Content = new StringContent(body) };
    }

    public void Fail(Guid id, bool timeout)
    {
        responses[id] = () =>
        {
            if (timeout)
            {
                throw new TaskCanceledException("Simulated Product timeout.");
            }

            throw new HttpRequestException("Simulated Product connection failure.");
        };
    }

    public int CallCount(Guid id) => calls.GetValueOrDefault(id);

    public void WaitForTwoLookups(Guid id)
    {
        gates[id] = new LookupGate();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Get || request.RequestUri is null
            || !request.RequestUri.AbsolutePath.StartsWith("/api/products/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected a Product GET request.");
        }

        var id = Guid.Parse(request.RequestUri.Segments[^1]);
        calls.AddOrUpdate(id, 1, (_, count) => count + 1);
        if (gates.TryGetValue(id, out var gate))
        {
            await gate.ArriveAsync(cancellationToken);
        }

        return responses.TryGetValue(id, out var createResponse)
            ? createResponse()
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private sealed class LookupGate
    {
        private int remaining = 2;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                ready.TrySetResult();
            }

            await ready.Task.WaitAsync(cancellationToken);
        }
    }
}
