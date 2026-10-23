using Shared.Contracts.Inventory;
using Xunit;

namespace InventoryService.Tests;

internal static class InventoryAssertions
{
    public static void EqualBalance(InventoryResponse expected, InventoryResponse? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ProductId, actual.ProductId);
        Assert.Equal(expected.OnHand, actual.OnHand);
        Assert.Equal(expected.Reserved, actual.Reserved);
        Assert.Equal(expected.AvailableQuantity, actual.AvailableQuantity);
        Assert.Equal(8, Convert.FromBase64String(actual.Version).Length);
    }
}
