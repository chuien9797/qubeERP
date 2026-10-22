using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Clients;
using Shared.Contracts.Inventory;
using Shared.Contracts.Products;
using Xunit;

namespace OrderService.Tests;

public sealed class DependencyClientTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unexpected")]
    [InlineData("price-scale")]
    [InlineData("negative-price")]
    [InlineData("empty-name")]
    [InlineData("null-products")]
    [InlineData("malformed-json")]
    [InlineData("missing-price")]
    [InlineData("server-error")]
    [InlineData("timeout")]
    public async Task InvalidProductResponsesAreUnavailable(string failure)
    {
        using var dependencies = new DependencyStub();
        var id = dependencies.AddProduct();
        var product = dependencies.Products[id];
        dependencies.ProductResponse = (_, _) => Task.FromResult(failure switch
        {
            "missing" => DependencyStub.Json(HttpStatusCode.OK, new ProductLookupResponse([], [])),
            "duplicate" => DependencyStub.Json(HttpStatusCode.OK, new ProductLookupResponse([product, product], [])),
            "unexpected" => DependencyStub.Json(HttpStatusCode.OK, new ProductLookupResponse([product with { Id = Guid.NewGuid() }], [])),
            "price-scale" => DependencyStub.Json(HttpStatusCode.OK, new ProductLookupResponse([product with { UnitPrice = 1.001m }], [])),
            "negative-price" => DependencyStub.Json(HttpStatusCode.OK, new ProductLookupResponse([product with { UnitPrice = -1m }], [])),
            "empty-name" => DependencyStub.Json(HttpStatusCode.OK, new ProductLookupResponse([product with { Name = " " }], [])),
            "null-products" => Raw(HttpStatusCode.OK, "{\"products\":null,\"missingProductIds\":[]}"),
            "malformed-json" => Raw(HttpStatusCode.OK, "{invalid"),
            "missing-price" => Raw(HttpStatusCode.OK, "{\"products\":[{\"id\":\"" + id + "\",\"sku\":\"SKU\",\"name\":\"Name\",\"isActive\":true}],\"missingProductIds\":[]}"),
            "timeout" => throw new TaskCanceledException("Dependency timeout"),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        });
        using var http = new HttpClient(dependencies) { BaseAddress = new Uri("http://dependencies.test") };
        var client = new ProductClient(http, NullLogger<ProductClient>.Instance);
        Assert.Null(await client.LookupAsync([id], CancellationToken.None));
    }

    [Fact]
    public async Task ProductResponsePreservesMissingAndInactiveIds()
    {
        using var dependencies = new DependencyStub();
        var inactive = dependencies.AddProduct(active: false);
        var missing = Guid.NewGuid();
        using var http = new HttpClient(dependencies) { BaseAddress = new Uri("http://dependencies.test") };
        var client = new ProductClient(http, NullLogger<ProductClient>.Instance);
        var lookup = (await client.LookupAsync([inactive, missing], CancellationToken.None))!;
        Assert.False(Assert.Single(lookup.Products).IsActive);
        Assert.Equal(missing, Assert.Single(lookup.MissingProductIds));
    }

    [Theory]
    [InlineData("created", InventoryOutcome.Reserved)]
    [InlineData("replay", InventoryOutcome.Reserved)]
    [InlineData("shortage", InventoryOutcome.Rejected)]
    [InlineData("wrong-id", InventoryOutcome.UnexpectedResponse)]
    [InlineData("wrong-shortfall", InventoryOutcome.UnexpectedResponse)]
    [InlineData("generic-conflict", InventoryOutcome.UnexpectedResponse)]
    [InlineData("malformed-json", InventoryOutcome.UnexpectedResponse)]
    [InlineData("unknown-status", InventoryOutcome.UnexpectedResponse)]
    [InlineData("server-error", InventoryOutcome.Uncertain)]
    [InlineData("http-timeout", InventoryOutcome.Uncertain)]
    [InlineData("timeout", InventoryOutcome.Uncertain)]
    [InlineData("disconnect", InventoryOutcome.Uncertain)]
    public async Task InventoryResponseMustMatchItsExplicitContract(string result, InventoryOutcome expected)
    {
        using var dependencies = new DependencyStub();
        var id = Guid.NewGuid();
        var product = Guid.NewGuid();
        dependencies.InventoryResponse = (_, _) => Task.FromResult(result switch
        {
            "created" => DependencyStub.Json(HttpStatusCode.Created, new ReservationResponse(id, "Reserved", null, [])),
            "replay" => DependencyStub.Json(HttpStatusCode.OK, new ReservationResponse(id, "Reserved", null, [])),
            "shortage" => DependencyStub.Json(HttpStatusCode.Conflict, new { errorCode = "INSUFFICIENT_STOCK", reservationId = id, shortfalls = new[] { new StockShortfall(product, 2, 1) } }),
            "wrong-shortfall" => DependencyStub.Json(HttpStatusCode.Conflict, new { errorCode = "INSUFFICIENT_STOCK", reservationId = id, shortfalls = new[] { new StockShortfall(product, 2, 5) } }),
            "wrong-id" => DependencyStub.Json(HttpStatusCode.Created, new ReservationResponse(Guid.NewGuid(), "Reserved", null, [])),
            "unknown-status" => DependencyStub.Json(HttpStatusCode.OK, new ReservationResponse(id, "Processing", null, [])),
            "generic-conflict" => Raw(HttpStatusCode.Conflict, "{}"),
            "malformed-json" => Raw(HttpStatusCode.Created, "{invalid"),
            "timeout" => throw new TaskCanceledException("Dependency timeout"),
            "disconnect" => throw new HttpRequestException("Response lost"),
            "http-timeout" => new HttpResponseMessage(HttpStatusCode.RequestTimeout),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        });
        using var http = new HttpClient(dependencies) { BaseAddress = new Uri("http://dependencies.test") };
        var client = new InventoryClient(http, NullLogger<InventoryClient>.Instance);
        var actual = await client.ReserveAsync(new CreateReservationRequest
        {
            ReservationId = id, Items = [new ReservationItemRequest { ProductId = product, Quantity = 2 }]
        }, CancellationToken.None);
        Assert.Equal(expected, actual.Outcome);
    }

    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        using var dependencies = new DependencyStub();
        dependencies.ProductResponse = (_, token) => throw new OperationCanceledException(token);
        dependencies.InventoryResponse = (_, token) => throw new OperationCanceledException(token);
        using var http = new HttpClient(dependencies) { BaseAddress = new Uri("http://dependencies.test") };
        var product = new ProductClient(http, NullLogger<ProductClient>.Instance);
        var inventory = new InventoryClient(http, NullLogger<InventoryClient>.Instance);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => product.LookupAsync([Guid.NewGuid()], source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inventory.ReserveAsync(new CreateReservationRequest
        {
            ReservationId = Guid.NewGuid(), Items = [new ReservationItemRequest { ProductId = Guid.NewGuid(), Quantity = 1 }]
        }, source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inventory.GetAsync(Guid.NewGuid(), [], source.Token));
    }

    [Theory]
    [InlineData("reserved", InventoryOutcome.Reserved)]
    [InlineData("rejected", InventoryOutcome.Rejected)]
    [InlineData("missing", InventoryOutcome.NotFound)]
    [InlineData("generic-404", InventoryOutcome.UnexpectedResponse)]
    [InlineData("wrong-404-id", InventoryOutcome.UnexpectedResponse)]
    [InlineData("empty-404", InventoryOutcome.UnexpectedResponse)]
    [InlineData("wrong-id", InventoryOutcome.UnexpectedResponse)]
    [InlineData("processing", InventoryOutcome.UnexpectedResponse)]
    [InlineData("wrong-shortfall", InventoryOutcome.UnexpectedResponse)]
    [InlineData("wrong-failure-code", InventoryOutcome.UnexpectedResponse)]
    [InlineData("invalid-request", InventoryOutcome.UnexpectedResponse)]
    [InlineData("forbidden", InventoryOutcome.UnexpectedResponse)]
    [InlineData("malformed-json", InventoryOutcome.UnexpectedResponse)]
    [InlineData("server-error", InventoryOutcome.Uncertain)]
    [InlineData("http-timeout", InventoryOutcome.Uncertain)]
    [InlineData("timeout", InventoryOutcome.Uncertain)]
    [InlineData("disconnect", InventoryOutcome.Uncertain)]
    public async Task ReservationQueryValidatesSavedOutcomesAndGenuineAbsence(string result, InventoryOutcome expected)
    {
        using var dependencies = new DependencyStub();
        var id = Guid.NewGuid();
        var product = Guid.NewGuid();
        dependencies.InventoryQueryResponse = (_, _) => Task.FromResult(result switch
        {
            "reserved" => DependencyStub.Json(HttpStatusCode.OK, new ReservationDetailsResponse(id, "Reserved", null, [], [new(product, 2)])),
            "rejected" => DependencyStub.Json(HttpStatusCode.OK, new ReservationDetailsResponse(id, "Rejected", "INSUFFICIENT_STOCK", [new StockShortfall(product, 2, 1)], [new(product, 2)])),
            "missing" => DependencyStub.Json(HttpStatusCode.NotFound, new { errorCode = "RESERVATION_NOT_FOUND", reservationId = id }),
            "generic-404" => DependencyStub.Json(HttpStatusCode.NotFound, new { errorCode = "NOT_FOUND", reservationId = id }),
            "wrong-404-id" => DependencyStub.Json(HttpStatusCode.NotFound, new { errorCode = "RESERVATION_NOT_FOUND", reservationId = Guid.NewGuid() }),
            "empty-404" => Raw(HttpStatusCode.NotFound, ""),
            "wrong-id" => DependencyStub.Json(HttpStatusCode.OK, new ReservationDetailsResponse(Guid.NewGuid(), "Reserved", null, [], [new(product, 2)])),
            "processing" => DependencyStub.Json(HttpStatusCode.OK, new ReservationDetailsResponse(id, "Processing", null, [], [new(product, 2)])),
            "wrong-shortfall" => DependencyStub.Json(HttpStatusCode.OK, new ReservationDetailsResponse(id, "Rejected", "INSUFFICIENT_STOCK", [new StockShortfall(product, 3, 1)], [new(product, 2)])),
            "wrong-failure-code" => DependencyStub.Json(HttpStatusCode.OK, new ReservationDetailsResponse(id, "Rejected", "UNKNOWN", [new StockShortfall(product, 2, 1)], [new(product, 2)])),
            "invalid-request" => DependencyStub.Json(HttpStatusCode.BadRequest, new { errorCode = "INVALID_RESERVATION" }),
            "forbidden" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            "malformed-json" => Raw(HttpStatusCode.OK, "{invalid"),
            "timeout" => throw new TaskCanceledException("Query timeout"),
            "disconnect" => throw new HttpRequestException("Query disconnected"),
            "http-timeout" => new HttpResponseMessage(HttpStatusCode.RequestTimeout),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        });
        using var http = new HttpClient(dependencies) { BaseAddress = new Uri("http://dependencies.test") };
        var client = new InventoryClient(http, NullLogger<InventoryClient>.Instance);
        var actual = await client.GetAsync(id, [new ReservationItemRequest { ProductId = product, Quantity = 2 }], CancellationToken.None);
        Assert.Equal(expected, actual.Outcome);
        Assert.Equal(0, dependencies.InventoryCalls);
        Assert.Equal(id, Assert.Single(dependencies.ReservationQueries));
    }

    private static HttpResponseMessage Raw(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
