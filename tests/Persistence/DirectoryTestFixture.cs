using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Persistence.Tests;

/// <summary>Explicit directory setup for existing session/role fixtures; it never creates credentials or grants.</summary>
internal static class DirectoryTestFixture
{
    internal static void Add(PlatformDbContext database, params Guid[] subjects)
    {
        var department = new DirectoryDepartment(database.TenantId, "fixture", "Fixture department");
        database.DirectoryTenants.Add(new DirectoryTenant(database.TenantId, "Fixture tenant"));
        database.Departments.Add(department);
        database.DirectoryUsers.AddRange(subjects.Distinct().Select(id =>
            new DirectoryUser(database.TenantId, id, $"FIXTURE.{id:N}", "Fixture subject", department.Id)));
    }

    internal static async Task RemoveAsync(PlatformDbContext database)
    {
        database.CommandReceipts.RemoveRange(await database.CommandReceipts.ToListAsync());
        database.DirectoryUsers.RemoveRange(await database.DirectoryUsers.ToListAsync());
        database.Departments.RemoveRange(await database.Departments.ToListAsync());
        database.DirectoryTenants.RemoveRange(await database.DirectoryTenants.ToListAsync());
    }
}
