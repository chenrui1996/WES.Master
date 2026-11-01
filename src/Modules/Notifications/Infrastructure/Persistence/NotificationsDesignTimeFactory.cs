using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wes.Notifications.Infrastructure.Persistence;

/// <summary>Connectionless EF metadata with the same owner migration history as runtime.</summary>
public sealed class NotificationsDesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    /// <inheritdoc />
    public NotificationsDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<NotificationsDbContext>().UseMySQL(
            mysql => mysql.MigrationsHistoryTable("__WesNotificationsMigrations")).Options,
        new Guid("00000000-0000-0000-0000-000000000001"));
}
