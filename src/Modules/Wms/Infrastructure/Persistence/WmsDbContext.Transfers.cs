using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.Transfers;

namespace Wes.Wms.Infrastructure.Persistence;

public sealed partial class WmsDbContext
{
    internal DbSet<TransferDocument> TransferDocuments => Set<TransferDocument>();
    internal DbSet<TransferFact> TransferFacts => Set<TransferFact>();
    internal DbSet<TransferOutbox> TransferOutbox => Set<TransferOutbox>();

    private void ConfigureTransfers(ModelBuilder builder)
    {
        var document = builder.Entity<TransferDocument>();
        document.ToTable("wms_transfer_documents");
        document.HasKey(row => new { row.TenantId, row.Id });
        document.HasQueryFilter(row => row.TenantId == TenantId);
        document.Ignore(row => row.Lines);
        document.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
        document.HasIndex(row => new { row.TenantId, row.CreationOperationId }).IsUnique();
        document.Property(row => row.Code).HasColumnType("varchar(64)").HasMaxLength(64).UseCollation("utf8mb4_bin");
        document.Property(row => row.Plan).HasColumnType("longtext");
        document.Property(row => row.State).HasConversion<string>().HasMaxLength(16);
        document.Property(row => row.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        document.HasOne<Warehouse>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.WarehouseId })
            .OnDelete(DeleteBehavior.Restrict);
        var fact = builder.Entity<TransferFact>();
        fact.ToTable("wms_transfer_facts");
        fact.HasKey(row => new { row.TenantId, row.OperationId });
        fact.HasQueryFilter(row => row.TenantId == TenantId);
        fact.HasIndex(row => new { row.TenantId, row.OrderId, row.Sequence }).IsUnique();
        fact.HasIndex(row => new { row.TenantId, row.OrderId, row.LineId, row.Kind }).IsUnique();
        fact.Property(row => row.Input).HasColumnType("longtext");
        fact.Property(row => row.Kind).HasConversion<string>().HasMaxLength(24);
        fact.Property(row => row.State).HasConversion<string>().HasMaxLength(16);
        fact.Property(row => row.AfterVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        fact.HasOne<TransferDocument>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.OrderId })
            .OnDelete(DeleteBehavior.Restrict);
        var outbox = builder.Entity<TransferOutbox>();
        outbox.ToTable("wms_transfer_outbox");
        outbox.HasKey(row => new { row.TenantId, row.OperationId });
        outbox.HasQueryFilter(row => row.TenantId == TenantId);
        outbox.Property(row => row.Payload).HasColumnType("longtext");
        outbox.HasIndex(row => new { row.TenantId, row.PostingId }).IsUnique();
        outbox.HasOne<TransferFact>().WithOne().HasForeignKey<TransferOutbox>(row => new { row.TenantId, row.OperationId })
            .OnDelete(DeleteBehavior.Restrict);
        outbox.HasOne<StockPosting>().WithOne().HasForeignKey<TransferOutbox>(row => new { row.TenantId, Id = row.PostingId })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ValidateTransferWrites()
    {
        var facts = ChangeTracker.Entries<TransferFact>().Where(row => row.State == EntityState.Added)
            .Select(row => row.Entity).ToArray();
        if (ChangeTracker.Entries<TransferFact>().Any(row => row.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Transfer facts are append-only.");
        foreach (var entry in ChangeTracker.Entries<TransferDocument>().Where(row => row.State != EntityState.Unchanged))
        {
            var document = entry.Entity;
            var fact = facts.SingleOrDefault(row => row.OrderId == document.Id && row.Sequence == document.Sequence);
            if (entry.State == EntityState.Deleted || Database.CurrentTransaction is null || fact is null
                || fact.AfterVersion != document.Version || fact.State != document.State)
                throw new InvalidOperationException("Transfer progress requires its exact permanent fact.");
            if (entry.State == EntityState.Added)
            {
                if (fact.Kind != TransferFactKind.Created || document.State != TransferState.Draft || document.Sequence != 1
                    || fact.OperationId != document.CreationOperationId || fact.ActorId != document.CreatorId
                    || document.Lines.Count is < 1 or > 100
                    || document.Lines.Select(row => row.Id).Distinct().Count() != document.Lines.Count
                    || document.Lines.Select(row => row.ExecutionId).Distinct().Count() != document.Lines.Count
                    || document.Lines.Select(row => row.ContainerId).Distinct().Count() != document.Lines.Count
                    || document.Lines.SelectMany(row => new[] { row.SourceBalanceId, row.TargetBalanceId }).Distinct().Count() != document.Lines.Count * 2
                    || document.Lines.Any(row => row.PlannedQuantity <= 0m || row.PlannedQuantity > StockMovementQuantity.MaximumValue
                        || row.Branch is < 0 or > 1 || row.SourceLocationId == row.TargetLocationId
                        || !QuantityPrecision.Create(row.Precision).CanRepresent(row.PlannedQuantity)))
                    throw new InvalidOperationException("Transfer creation must retain a legal immutable plan.");
                continue;
            }
            if (entry.Properties.Any(row => row.IsModified && row.Metadata.Name
                    is not (nameof(TransferDocument.State) or nameof(TransferDocument.Sequence) or nameof(TransferDocument.Version)))
                || document.Sequence != entry.Property(row => row.Sequence).OriginalValue + 1
                || document.Version == entry.Property(row => row.Version).OriginalValue)
                throw new InvalidOperationException("Transfer original identity and plan are immutable.");
            var previous = entry.Property(row => row.State).OriginalValue;
            if (!(fact.Kind switch
            {
                TransferFactKind.Approved => previous == TransferState.Draft && document.State == TransferState.Approved,
                TransferFactKind.Dispatched or TransferFactKind.Arrived or TransferFactKind.Unknown =>
                    previous is TransferState.Approved or TransferState.Executing && document.State == TransferState.Executing,
                TransferFactKind.Synced => previous == TransferState.Executing && document.State is TransferState.Executing or TransferState.Completed,
                TransferFactKind.Closed => previous is TransferState.Draft or TransferState.Approved or TransferState.Executing
                    && document.State is TransferState.Cancelled or TransferState.ClosedPartial,
                _ => false
            }))
                throw new InvalidOperationException("Transfer fact does not explain its state transition.");
            var prior = TransferFacts.AsNoTracking().Where(row => row.OrderId == document.Id).OrderBy(row => row.Sequence).ToArray();
            TransferProgress.Validate(document, prior.Append(fact).ToArray());
            if (prior.Length != entry.Property(row => row.Sequence).OriginalValue
                || prior[^1].AfterVersion != entry.Property(row => row.Version).OriginalValue)
                throw new InvalidOperationException("Transfer updates must extend the current exact history.");
            using (var originalInput = JsonDocument.Parse(fact.Input))
            {
                var input = originalInput.RootElement;
                if (!input.TryGetProperty("ExpectedVersion", out var version) || version.ValueKind != JsonValueKind.String
                    || version.GetString() != prior[^1].AfterVersion
                    || fact.LineId != Guid.Empty && (!input.TryGetProperty("LineId", out var line)
                        || !line.TryGetGuid(out var lineId) || lineId != fact.LineId))
                    throw new InvalidOperationException("Transfer facts must retain their exact original expected version and execution selection.");
                if (fact.Kind is TransferFactKind.Arrived or TransferFactKind.Unknown or TransferFactKind.Closed)
                {
                    if (!input.TryGetProperty("Reason", out var reason) || reason.ValueKind != JsonValueKind.String
                        || reason.GetString() is not { Length: > 0 and <= 256 } text || text != text.Trim() || text.Any(char.IsControl))
                        throw new InvalidOperationException("Transfer evidence and closure facts require a bounded original reason.");
                }
            }
            if (fact.Kind is TransferFactKind.Dispatched or TransferFactKind.Unknown)
            {
                var line = document.Lines.SingleOrDefault(row => row.Id == fact.LineId);
                var action = fact.Kind == TransferFactKind.Dispatched ? ReservationAction.MarkInTransit : ReservationAction.MarkUnknown;
                var required = fact.Kind == TransferFactKind.Dispatched ? TransferExecutionStatus.Unstarted : TransferExecutionStatus.InTransit;
                if (line is null || line.Branch != 0 || TransferProgress.For(prior, line.Id).Execution != required
                    || ChangeTracker.Entries<ReservationChange>().Count(row => row.State == EntityState.Added
                        && row.Entity.ExecutionUnitId == line.ExecutionId && row.Entity.Action == action
                        && row.Entity.ActorId == fact.ActorId && row.Entity.CorrelationId == fact.CorrelationId) != 1)
                    throw new InvalidOperationException("Transfer dispatch and unknown require their exact protected reservation transition.");
            }
            if (fact.Kind == TransferFactKind.Arrived)
            {
                var line = document.Lines.SingleOrDefault(row => row.Id == fact.LineId);
                var input = JsonSerializer.Deserialize<TransferArrivalRequest>(fact.Input);
                if (line is null || line.Branch != 0 || input is null || input.LineId != line.Id
                    || input.SourceLocationId != line.SourceLocationId || input.TargetLocationId != line.TargetLocationId
                    || input.ContainerId != line.ContainerId
                    || !decimal.TryParse(input.Quantity, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var quantity)
                    || quantity != line.PlannedQuantity || TransferProgress.For(prior, line.Id).Execution != TransferExecutionStatus.InTransit)
                    throw new InvalidOperationException("Transfer arrival requires exact durable final scans of its in-transit whole unit.");
            }
            if (fact.Kind == TransferFactKind.Closed)
            {
                var moved = document.Lines.Any(line => TransferProgress.For(prior, line.Id).Posting == TransferPostingStatus.Synced);
                if (document.State != (moved ? TransferState.ClosedPartial : TransferState.Cancelled)
                    || document.Lines.Any(line =>
                    {
                        var progress = TransferProgress.For(prior, line.Id);
                        return progress.Execution is TransferExecutionStatus.InTransit or TransferExecutionStatus.Unknown
                            || progress.Execution == TransferExecutionStatus.Arrived && progress.Posting != TransferPostingStatus.Synced;
                    }))
                    throw new InvalidOperationException("A transfer closure must retain unresolved effects.");
                if (previous != TransferState.Draft && document.Lines.Where(line =>
                        TransferProgress.For(prior, line.Id).Execution == TransferExecutionStatus.Unstarted).Any(line =>
                        ChangeTracker.Entries<ReservationChange>().Count(row => row.State == EntityState.Added
                            && row.Entity.ExecutionUnitId == line.ExecutionId && row.Entity.Action == ReservationAction.ReleaseUnstarted
                            && row.Entity.Quantity == line.PlannedQuantity) != 1))
                    throw new InvalidOperationException("A safe transfer closure must release every exact unstarted source.");
            }
            if (fact.Kind == TransferFactKind.Approved)
            {
                var reserved = ChangeTracker.Entries<StockReservation>().Where(row => row.State == EntityState.Added)
                    .Select(row => row.Entity).ToArray();
                if (reserved.Length != document.Lines.Count || document.Lines.Any(line => reserved.Count(row =>
                        row.SourceDocumentId == document.Id && row.SourceLineId == line.Id && row.ExecutionUnitId == line.ExecutionId
                        && row.BalanceId == line.SourceBalanceId && row.OriginalQuantity == line.PlannedQuantity) != 1)
                    || document.Lines.Any(line =>
                    {
                        var positive = Balances.AsNoTracking().Where(row => row.ContainerId == line.ContainerId && row.Quantity > 0m).ToArray();
                        return positive.Length != 1 || positive[0].Id != line.SourceBalanceId || positive[0].Quantity != line.PlannedQuantity
                            || Balances.AsNoTracking().Any(row => row.WarehouseId == document.WarehouseId
                                && row.LocationId == line.TargetLocationId && row.Quantity > 0m);
                    }))
                    throw new InvalidOperationException("Transfer approval must reserve all exact lines atomically.");
            }
            if (fact.Kind == TransferFactKind.Synced)
            {
                var line = document.Lines.SingleOrDefault(row => row.Id == fact.LineId);
                if (line is null || ChangeTracker.Entries<StockPosting>().Count(row => row.State == EntityState.Added
                        && ExactTransfer(row.Entity, document, line, fact)) != 1
                    || TransferProgress.For(prior, line.Id).Posting != TransferPostingStatus.Pending
                    || (document.State == TransferState.Completed) != document.Lines.All(candidate =>
                        candidate.Id == line.Id || TransferProgress.For(prior, candidate.Id).Posting == TransferPostingStatus.Synced))
                    throw new InvalidOperationException("Synced transfer progress requires its exact unique ledger effect.");
                var posting = ChangeTracker.Entries<StockPosting>().Single(row => row.State == EntityState.Added
                    && ExactTransfer(row.Entity, document, line, fact)).Entity;
                if (!ChangeTracker.Entries<StockEntry>().Any(row => row.State == EntityState.Added && row.Entity.PostingId == posting.Id
                        && row.Entity.BalanceId == line.SourceBalanceId && row.Entity.Before == line.PlannedQuantity && row.Entity.After == 0m)
                    || !ChangeTracker.Entries<StockEntry>().Any(row => row.State == EntityState.Added && row.Entity.PostingId == posting.Id
                        && row.Entity.BalanceId == line.TargetBalanceId && row.Entity.Before == 0m && row.Entity.After == line.PlannedQuantity))
                    throw new InvalidOperationException("Transfer synchronization must empty the exact source whole unit into an empty target.");
                var staged = ChangeTracker.Entries<TransferOutbox>().SingleOrDefault(row => row.Entity.OperationId == fact.OperationId);
                if (staged is null) TransferOutbox.Add(new(fact, posting));
                else if (staged.State != EntityState.Added || !staged.Entity.Matches(fact, posting))
                    throw new InvalidOperationException("Transfer outbox must match its exact committed ledger effect.");
            }
        }
        foreach (var fact in facts)
            if (!ChangeTracker.Entries<TransferDocument>().Any(row => row.State is EntityState.Added or EntityState.Modified
                && row.Entity.Id == fact.OrderId && row.Entity.Sequence == fact.Sequence && row.Entity.Version == fact.AfterVersion))
                throw new InvalidOperationException("Transfer facts must advance their exact document.");
        foreach (var posting in ChangeTracker.Entries<StockPosting>().Where(row => row.State == EntityState.Added).Select(row => row.Entity))
        {
            var document = TransferDocuments.AsNoTracking().SingleOrDefault(row => row.Id == posting.SourceDocumentId);
            if (document is not null && !facts.Any(row => row.OrderId == document.Id && row.Kind == TransferFactKind.Synced
                && document.Lines.Any(line => ExactTransfer(posting, document, line, row))))
                throw new InvalidOperationException("A transfer ledger effect requires its exact owner synchronization.");
            var balances = Balances.AsNoTracking().Where(row => row.Id == posting.SourceBalanceId || row.Id == posting.TargetBalanceId).ToArray();
            var active = TransferDocuments.AsNoTracking().Where(row => row.State == TransferState.Approved || row.State == TransferState.Executing).ToArray();
            foreach (var protectedDocument in active)
            {
                if (facts.Any(fact => fact.Kind == TransferFactKind.Synced
                    && protectedDocument.Lines.Any(line => ExactTransfer(posting, protectedDocument, line, fact)))) continue;
                var history = TransferFacts.AsNoTracking().Where(row => row.OrderId == protectedDocument.Id).ToArray();
                foreach (var line in protectedDocument.Lines)
                {
                    var progress = TransferProgress.For(history, line.Id);
                    if (progress.Posting == TransferPostingStatus.Synced || progress.Execution == TransferExecutionStatus.Closed) continue;
                    if (balances.Any(balance => balance.ContainerId == line.ContainerId
                            || balance.WarehouseId == protectedDocument.WarehouseId
                                && (balance.LocationId == line.SourceLocationId || balance.LocationId == line.TargetLocationId))
                        && !facts.Any(fact => fact.Kind == TransferFactKind.Synced && ExactTransfer(posting, protectedDocument, line, fact)))
                        throw new InvalidOperationException("Protected transfer resources can only be posted by their exact arrived unit.");
                }
            }
        }
        foreach (var reservation in ChangeTracker.Entries<StockReservation>().Where(row => row.State is EntityState.Added or EntityState.Modified))
        {
            var value = reservation.Entity;
            var document = TransferDocuments.AsNoTracking().SingleOrDefault(row => row.Id == value.SourceDocumentId);
            if (document is null) continue;
            var line = document.Lines.SingleOrDefault(row => row.Id == value.SourceLineId);
            var kind = reservation.State == EntityState.Added ? TransferFactKind.Approved
                : value.RemainingQuantity == 0m
                    ? ChangeTracker.Entries<StockPosting>().Any(row => row.State == EntityState.Added
                        && row.Entity.ExecutionUnitId == value.ExecutionUnitId) ? TransferFactKind.Synced : TransferFactKind.Closed
                    : (value.Kind & StockProtectionKind.Unknown) != 0 ? TransferFactKind.Unknown : TransferFactKind.Dispatched;
            if (line is null || line.ExecutionId != value.ExecutionUnitId || line.SourceBalanceId != value.BalanceId
                || value.OriginalQuantity != line.PlannedQuantity
                || !facts.Any(fact => fact.OrderId == document.Id && fact.Kind == kind
                    && (kind is TransferFactKind.Approved or TransferFactKind.Closed || fact.LineId == line.Id)))
                throw new InvalidOperationException("Transfer reservation effects require their exact same-transaction owner fact.");
        }
        foreach (var outbox in ChangeTracker.Entries<TransferOutbox>().Where(row => row.State != EntityState.Unchanged))
        {
            if (outbox.State != EntityState.Added || !facts.Any(fact => fact.Kind == TransferFactKind.Synced
                && fact.OperationId == outbox.Entity.OperationId
                && ChangeTracker.Entries<StockPosting>().Any(row => row.State == EntityState.Added
                    && row.Entity.Id == outbox.Entity.PostingId && outbox.Entity.Matches(fact, row.Entity))))
                throw new InvalidOperationException("Transfer pending outbox is immutable and requires its exact new source fact.");
        }
    }

    private static bool ExactTransfer(StockPosting posting, TransferDocument document, TransferPlanLine line, TransferFact fact) =>
        posting.Kind == StockPostingKind.Transfer && posting.OperationId == fact.OperationId && fact.LineId == line.Id
        && posting.SourceDocumentId == document.Id && posting.SourceLineId == line.Id && posting.ExecutionUnitId == line.ExecutionId
        && posting.SourceBalanceId == line.SourceBalanceId && posting.TargetBalanceId == line.TargetBalanceId
        && posting.Quantity == line.PlannedQuantity && posting.ActorId == fact.ActorId && posting.CorrelationId == fact.CorrelationId;
}
