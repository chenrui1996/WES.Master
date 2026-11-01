using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wes.WorkflowForm.Infrastructure.Persistence;

/// <summary>Connectionless migration metadata only; runtime tenants come from a trusted owner boundary.</summary>
public sealed class WorkflowFormDesignTimeFactory : IDesignTimeDbContextFactory<WorkflowFormDbContext>
{
    /// <inheritdoc />
    public WorkflowFormDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<WorkflowFormDbContext>()
            .UseMySQL(mysql => mysql.MigrationsHistoryTable("__WesWorkflowFormMigrations")).Options,
            new Guid("00000000-0000-0000-0000-000000000001"));
}
