using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Clients;
using Shared.Contracts.Inventory;
using Xunit;

namespace OrderService.Tests;

public sealed class ReleaseClientTests
{
    [Theory]
    [InlineData(500, InventoryOutcome.Uncertain)]
    [InlineData(503, InventoryOutcome.Uncertain)]
    [InlineData(408, InventoryOutcome.Uncertain)]
    [InlineData(404, InventoryOutcome.NotFound)]
    [InlineData(409, InventoryOutcome.ReleaseConflict)]
    [InlineData(400, InventoryOutcome.UnexpectedResponse)]
    [InlineData(422, InventoryOutcome.UnexpectedResponse)]
    [InlineData(202, InventoryOutcome.UnexpectedResponse)]
    public async Task ReleaseClassifiesHttpOutcomes(int code, InventoryOutcome expected)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)))) { BaseAddress = new Uri("http://inventory.test") };
        var client = new InventoryClient(http, NullLogger<InventoryClient>.Instance);
        Assert.Equal(expected, (await client.ReleaseAsync(Guid.NewGuid(), "Reason", CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("Released", false, InventoryOutcome.Released)]
    [InlineData("Reserved", false, InventoryOutcome.UnexpectedResponse)]
    [InlineData("Reserved", true, InventoryOutcome.Reserved)]
    [InlineData("Released", true, InventoryOutcome.Released)]
    [InlineData("Consumed", true, InventoryOutcome.ReleaseConflict)]
    [InlineData("Other", true, InventoryOutcome.UnexpectedResponse)]
    public async Task SuccessRequiresMatchingIdentityAndExpectedState(string status, bool query, InventoryOutcome expected)
    {
        var id = Guid.NewGuid();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal(query ? HttpMethod.Get : HttpMethod.Post, request.Method);
            Assert.Equal($"/api/inventory/reservations/{id}" + (query ? "" : "/release"), request.RequestUri!.AbsolutePath);
            if (!query) Assert.Equal("Saved reason", (await request.Content!.ReadFromJsonAsync<ReleaseReservationRequest>(token))!.Reason);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ReservationResponse(id, status, null, [])) };
        })) { BaseAddress = new Uri("http://inventory.test") };
        var client = new InventoryClient(http, NullLogger<InventoryClient>.Instance);
        Assert.Equal(expected, (query ? await client.GetReleaseStatusAsync(id, CancellationToken.None)
            : await client.ReleaseAsync(id, "Saved reason", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task UnrelatedReservationCannotProveRelease()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new ReservationResponse(Guid.NewGuid(), "Released", null, [])) }))) { BaseAddress = new Uri("http://inventory.test") };
        var client = new InventoryClient(http, NullLogger<InventoryClient>.Instance);
        Assert.Equal(InventoryOutcome.UnexpectedResponse, (await client.ReleaseAsync(Guid.NewGuid(), "Reason", CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostConnectionAndTimeoutRemainUncertain(bool query)
    {
        foreach (var error in new Exception[] { new HttpRequestException("Lost response"), new TaskCanceledException("Timeout") })
        {
            using var http = new HttpClient(new Handler((_, _) => throw error)) { BaseAddress = new Uri("http://inventory.test") };
            var client = new InventoryClient(http, NullLogger<InventoryClient>.Instance);
            Assert.Equal(InventoryOutcome.Uncertain, (query ? await client.GetReleaseStatusAsync(Guid.NewGuid(), CancellationToken.None)
                : await client.ReleaseAsync(Guid.NewGuid(), "Reason", CancellationToken.None)).Outcome);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
