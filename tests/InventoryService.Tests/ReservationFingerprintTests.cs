using System.Globalization;
using InventoryService.Application;
using Shared.Contracts.Inventory;
using Xunit;

namespace InventoryService.Tests;

public sealed class ReservationFingerprintTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("tr-TR")]
    public void FingerprintHasStableVersionedFormatAcrossCultures(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var items = new[]
            {
                new ReservationItemRequest { ProductId = Guid.Parse("22222222-2222-2222-2222-222222222222"), Quantity = 2 },
                new ReservationItemRequest { ProductId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Quantity = 3 }
            };
            Assert.Equal(ExpectedFingerprint, ReservationFingerprint.Create(items));
            Assert.Equal(ExpectedFingerprint, ReservationFingerprint.Create(items.Reverse()));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DifferentQuantityOrProductChangesFingerprint()
    {
        var id = Guid.NewGuid();
        var original = ReservationFingerprint.Create([new ReservationItemRequest { ProductId = id, Quantity = 1 }]);
        Assert.NotEqual(original, ReservationFingerprint.Create([new ReservationItemRequest { ProductId = id, Quantity = 2 }]));
        Assert.NotEqual(original, ReservationFingerprint.Create([new ReservationItemRequest { ProductId = Guid.NewGuid(), Quantity = 1 }]));
    }

    private const string ExpectedFingerprint = "f15b134b1f1b23372f5b6143898374dc1728437ae167c5617f679313b31afc5b";
}
