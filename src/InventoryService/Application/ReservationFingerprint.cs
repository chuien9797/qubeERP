using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Shared.Contracts.Inventory;

namespace InventoryService.Application;

public static class ReservationFingerprint
{
    public static string Create(IEnumerable<ReservationItemRequest> items)
    {
        var lines = items.OrderBy(item => item.ProductId.ToString("D"), StringComparer.Ordinal)
            .Select(item => item.ProductId.ToString("D") + ":" + item.Quantity.ToString(CultureInfo.InvariantCulture));
        var canonical = "reservation:v1\n" + string.Join("\n", lines);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
