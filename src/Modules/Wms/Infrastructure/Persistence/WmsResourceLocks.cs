using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Wes.Wms.Infrastructure.Persistence;

internal static class WmsResourceLocks
{
    internal static async Task AcquireAsync(WmsDbContext database, Guid warehouse, IEnumerable<Guid> locations,
        Guid? container, CancellationToken ct)
    {
        await FenceAsync(database, "wms_warehouses", warehouse, ct);
        foreach (var location in locations.Distinct().Order()) await FenceAsync(database, "wms_locations", location, ct);
        if (container is not null) await FenceAsync(database, "wms_stock_containers", container.Value, ct);
    }

    private static async Task FenceAsync(WmsDbContext database, string table, Guid id, CancellationToken ct)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Operational resource fences require their owner transaction.");
        command.CommandText = $"SELECT Id FROM {table} WHERE TenantId = @tenant AND Id = @id FOR UPDATE";
        var tenant = command.CreateParameter(); tenant.ParameterName = "@tenant"; tenant.Value = database.TenantId.ToString("D"); command.Parameters.Add(tenant);
        var resource = command.CreateParameter(); resource.ParameterName = "@id"; resource.Value = id.ToString("D"); command.Parameters.Add(resource);
        if (await command.ExecuteScalarAsync(ct) is not string)
            throw new InvalidOperationException("Immutable resource provenance lost its registered identity.");
    }
}
