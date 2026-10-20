using Microsoft.EntityFrameworkCore;

namespace InventoryService.Data;

public sealed class InventoryServiceContext(DbContextOptions<InventoryServiceContext> options) : DbContext(options);
