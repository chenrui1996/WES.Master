using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Wes.Persistence.Tests.Migrations;

/// <summary>Adds tenant-scoped uniqueness without rewriting the already applied initial migration.</summary>
[DbContext(typeof(ProbeContext))]
[Migration("202610230002_ProbeConstraints")]
public sealed class ProbeConstraints : Migration
{
    /// <summary>Only the synthetic probe table is modified; existing business objects are never inspected.</summary>
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateIndex(
            name: "UX_probe_run_tenant_code",
            table: "wes_foundation_probe",
            columns: ["RunId", "TenantId", "Code"],
            unique: true);

    /// <summary>Provided for script review; shared-instance tests never execute Down.</summary>
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropIndex(name: "UX_probe_run_tenant_code", table: "wes_foundation_probe");

    /// <summary>Freezes this migration's metadata separately from the runtime context.</summary>
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildSchema(modelBuilder);

    internal static void BuildSchema(ModelBuilder modelBuilder)
    {
        FoundationProbe.BuildSchema(modelBuilder);
        var row = modelBuilder.Entity<ProbeRow>();
        row.Property(item => item.Version).IsConcurrencyToken();
        row.HasIndex(item => new { item.RunId, item.TenantId, item.Code })
            .IsUnique().HasDatabaseName("UX_probe_run_tenant_code");
    }
}
