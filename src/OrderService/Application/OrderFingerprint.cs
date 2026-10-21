using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Shared.Contracts.Orders;

namespace OrderService.Application;

public static class OrderFingerprint
{
    public static string Create(IEnumerable<OrderItemRequest> items)
    {
        var lines = items.OrderBy(item => item.ProductId.ToString("D"), StringComparer.Ordinal)
            .Select(item => item.ProductId.ToString("D") + ":" + item.Quantity.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("order:v1\n" + string.Join("\n", lines))));
    }
}
