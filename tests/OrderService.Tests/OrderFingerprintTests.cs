using System.Globalization;
using OrderService.Application;
using Shared.Contracts.Orders;
using Xunit;

namespace OrderService.Tests;

public sealed class OrderFingerprintTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("tr-TR")]
    public void FingerprintIsInvariantAndIgnoresItemOrder(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var items = new[]
            {
                new OrderItemRequest { ProductId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Quantity = 3 },
                new OrderItemRequest { ProductId = Guid.Parse("22222222-2222-2222-2222-222222222222"), Quantity = 2 }
            };
            Assert.Equal(OrderFingerprint.Create(items), OrderFingerprint.Create(items.Reverse()));
            Assert.Equal("101aacb92fa4710d0bc741ebab6921f9157d87206580175c74421e1ebdf62a6f", OrderFingerprint.Create(items));
            Assert.Matches("^[0-9a-f]{64}$", OrderFingerprint.Create(items));
            Assert.NotEqual(OrderFingerprint.Create(items), OrderFingerprint.Create([
                new OrderItemRequest { ProductId = items[0].ProductId, Quantity = 4 }, items[1]]));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
