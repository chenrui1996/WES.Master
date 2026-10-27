using System.Collections.Immutable;
using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Domain;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Inbound;

internal sealed partial class InboundAccess
{
    internal async Task<InboundPage> QueryAsync(InboundSubject subject, InboundQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Inbound catalogs require a clean independent WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return RejectedPage(InboundReadDecision.Forbidden);
        if (query.Offset is < 0 or > 10000 || query.Limit is not (25 or 50 or 100) || query.WarehouseId == Guid.Empty
            || query.Code is not null && !MasterDataValue.IsCode(query.Code)
            || query.State.HasValue && !Enum.IsDefined(query.State.Value))
            return RejectedPage(InboundReadDecision.Invalid);
        try
        {
            var before = await CreatorsAsync(subject, ct);
            if (!before.Authorized) return RejectedPage(InboundReadDecision.Forbidden);
            // Match the existing MySQL owner pattern: Guid primitive Contains is not reliably translated by this provider.
            var parameter = Expression.Parameter(typeof(InboundDocument), "document");
            Expression allowed = Expression.Constant(false);
            foreach (var creator in before.CreatorIds)
                allowed = Expression.OrElse(allowed, Expression.Equal(Expression.Property(parameter, nameof(InboundDocument.CreatorId)),
                    Expression.Constant(creator)));
            var documents = database.InboundDocuments.AsNoTracking()
                .Where(Expression.Lambda<Func<InboundDocument, bool>>(allowed, parameter));
            if (query.Code is not null)
            {
                var code = query.Code.ToUpperInvariant();
                documents = documents.Where(item => item.Code == code);
            }
            if (query.WarehouseId.HasValue) documents = documents.Where(item => item.WarehouseId == query.WarehouseId);
            if (query.State.HasValue)
            {
                var state = query.State.Value switch
                {
                    InboundState.Draft => InboundOrderState.Draft,
                    InboundState.Approved => InboundOrderState.Approved,
                    InboundState.Executing => InboundOrderState.Executing,
                    InboundState.Completed => InboundOrderState.Completed,
                    _ => throw new InvalidOperationException("Unsupported inbound query state.")
                };
                documents = documents.Where(item => item.State == state);
            }
            var rows = await documents.OrderBy(item => item.Code).ThenBy(item => item.Id)
                .Skip(query.Offset).Take(query.Limit)
                .Select(item => new { item.Id, item.Code, item.WarehouseId, item.State, item.Version, item.CreatedAt, item.CreatorId })
                .ToListAsync(ct);
            var after = await CreatorsAsync(subject, ct);
            if (!after.Authorized || rows.Any(row => !after.CreatorIds.Contains(row.CreatorId)))
                return RejectedPage(InboundReadDecision.Forbidden);
            ct.ThrowIfCancellationRequested();
            return new(InboundReadDecision.Success, rows.Select(row =>
                new InboundSummary(row.Id, row.Code, row.WarehouseId, State(row.State), row.Version, row.CreatedAt))
                .ToImmutableArray(), query.Offset, query.Limit);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS inbound catalog failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
        catch (InvalidOperationException)
        {
            logger.LogError("WMS inbound catalog rejected inconsistent state {Code}", "InboundCatalogInvalid");
            throw;
        }

        InboundPage RejectedPage(InboundReadDecision decision)
        {
            logger.LogWarning("WMS inbound catalog rejected {Code}", decision.ToString());
            return new(decision, [], query.Offset, query.Limit);
        }
    }

    private async Task<InboundCreatorAccess> CreatorsAsync(InboundSubject subject, CancellationToken ct)
    {
        var result = await authorization.ReadCreatorsAsync(subject, Fields, ct);
        ct.ThrowIfCancellationRequested();
        if (result is null || result.CreatorIds is null || result.CreatorIds.Contains(Guid.Empty))
            throw new InvalidOperationException("Inbound catalog authority returned an invalid decision.");
        return result with { CreatorIds = result.CreatorIds.ToHashSet() };
    }
}
