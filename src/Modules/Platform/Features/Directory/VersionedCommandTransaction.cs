using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Settings;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Directory;

/// <summary>Shared role/setting transaction: current authorization, mutation, audit and optional safe receipt commit together.</summary>
internal static class VersionedCommandTransaction
{
    internal static async Task<SettingWriteResult> RunAsync(PlatformDbContext database, TimeProvider clock, ILogger logger,
        TenantContext context, DirectoryOperation? operation, DirectoryCommandKind kind, Guid target, object?[] arguments,
        Func<Task<SettingWriteResult>> mutation, Func<Task<bool>> replayAllowed, CancellationToken ct)
    {
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null
            || database.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException("Versioned commands require a clean independent owner unit of work.");
        if (operation?.OperationId == Guid.Empty) throw new ArgumentException("An operation UUID is required.");
        var fingerprint = operation is null ? null
            : DirectoryCommandReceipts.Fingerprint(kind, [context.TenantId, context.ActorId, target, .. arguments]);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Serializable missing-key reads may deadlock; each retry owns a fresh transaction and rereads authority.
                await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                if ((await new CurrentAuthorization(database, clock).ReadGrantsAsync(context, ct)).IsEmpty)
                    return new(SettingDecision.Forbidden);
                if (operation is not null)
                {
                    var receipt = await DirectoryCommandReceipts.FindAsync(database, context, operation, clock.GetUtcNow(), ct);
                    if (receipt is not null)
                    {
                        if (receipt.Kind != kind || receipt.Fingerprint != fingerprint) return new(SettingDecision.IdempotencyConflict);
                        if (!await replayAllowed()) return new(SettingDecision.Forbidden);
                        return new(SettingDecision.Changed, receipt.ResultVersion);
                    }
                }
                var result = await mutation();
                if (result.Decision != SettingDecision.Changed) return result;
                if (operation is not null)
                    DirectoryCommandReceipts.Stage(database, context, operation, kind, fingerprint!, target, result.Version!, clock.GetUtcNow());
                await database.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (operation is not null && attempt < 4) { }
            catch (DbUpdateConcurrencyException) { return new(SettingDecision.VersionConflict); }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 or 1213 or 1205 }
                && operation is not null && attempt < 4)
            { }
            catch (MySqlException error) when (error.Number is 1213 or 1205 && operation is not null && attempt < 4) { }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1213 or 1205 })
            { return new(SettingDecision.VersionConflict); }
            catch (MySqlException error) when (error.Number is 1213 or 1205)
            { return new(SettingDecision.VersionConflict); }
            finally { database.ChangeTracker.Clear(); }
            logger.LogWarning("Versioned command retry {Kind} {Attempt}", kind, attempt + 1);
            await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), ct);
        }
    }
}
