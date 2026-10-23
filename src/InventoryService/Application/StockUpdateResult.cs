using Shared.Contracts.Inventory;

namespace InventoryService.Application;

public enum StockUpdateStatus
{
    Updated,
    ProductNotFound,
    ProductUnavailable,
    StockNotFound,
    BelowReserved,
    VersionConflict
}

public sealed record StockUpdateResult(StockUpdateStatus Status, InventoryResponse? Stock = null);
