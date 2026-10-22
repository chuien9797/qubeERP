using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Clients;
using Shared.Contracts.Inventory;
using Xunit;

namespace OrderService.Tests;

public sealed class ReservationRecoveryContractTests
{
    [Theory]
    [InlineData("quantity")]
    [InlineData("product")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("absent")]
    public async Task MatchingReservationIdCannotProveTheWrongItemsAreReserved(string failure)
    {
        var id = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        ReservationItemResponse[]? items = failure switch
        {
            "quantity" => [new(first, 1), new(second, 3)],
            "product" => [new(Guid.NewGuid(), 2), new(second, 3)],
            "missing" => [new(first, 2)],
            "duplicate" => [new(first, 2), new(first, 2)],
            "null" => [null!, new(second, 3)],
            _ => null
        };
        using var http = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ReservationDetailsResponse(id, "Reserved", null, [], items!))
        })) { BaseAddress = new Uri("http://inventory.test") };
        var client = new InventoryClient(http, NullLogger<InventoryClient>.Instance);
        var result = await client.GetAsync(id, [new() { ProductId = first, Quantity = 2 }, new() { ProductId = second, Quantity = 3 }], CancellationToken.None);
        Assert.Equal(InventoryOutcome.UnexpectedResponse, result.Outcome);
    }

    [Fact]
    public async Task MatchingMultiProductLinesAreAcceptedInAnyOrder()
    {
        var id = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using var http = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ReservationDetailsResponse(id, "Reserved", null, [], [new(second, 3), new(first, 2)]))
        })) { BaseAddress = new Uri("http://inventory.test") };
        var result = await new InventoryClient(http, NullLogger<InventoryClient>.Instance).GetAsync(id,
            [new() { ProductId = first, Quantity = 2 }, new() { ProductId = second, Quantity = 3 }], CancellationToken.None);
        Assert.Equal(InventoryOutcome.Reserved, result.Outcome);
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }
}
