using InventoryService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace InventoryService.Health;

public sealed class DatabaseHealthCheck(IDbContextFactory<InventoryServiceContext> contextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await database.Database.CanConnectAsync(cancellationToken))
        {
            return HealthCheckResult.Healthy();
        }

        return HealthCheckResult.Unhealthy("The service database is unavailable.");
    }
}
