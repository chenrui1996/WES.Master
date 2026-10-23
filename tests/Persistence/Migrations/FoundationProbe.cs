using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Wes.Persistence.Tests.Migrations;

/// <summary>Creates only the synthetic provider probe table inside the pre-existing wes_test database.</summary>
[DbContext(typeof(ProbeContext))]
[Migration("202610230001_FoundationProbe")]
public sealed class FoundationProbe : Migration
{
    /// <summary>No database-level or business-owner objects are created.</summary>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "wes_foundation_probe",
            columns: table => new
            {
                RunId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                TenantId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                Id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                Quantity = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_wes_foundation_probe", item => new { item.RunId, item.TenantId, item.Id }));
    }

    /// <summary>Provided for script review only; shared-instance tests never execute Down.</summary>
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "wes_foundation_probe");

    /// <summary>Preserves the migration's original schema independently of runtime context state.</summary>
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildSchema(modelBuilder);

    internal static void BuildSchema(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.7");
        var row = modelBuilder.Entity<ProbeRow>();
        row.ToTable("wes_foundation_probe");
        row.HasKey(item => new { item.RunId, item.TenantId, item.Id });
        row.Property(item => item.RunId).HasColumnType("varchar(32)").HasMaxLength(32).IsRequired();
        row.Property(item => item.TenantId).HasColumnType("varchar(32)").HasMaxLength(32).IsRequired();
        row.Property(item => item.Id).HasColumnType("varchar(32)").HasMaxLength(32).IsRequired();
        row.Property(item => item.Code).HasColumnType("varchar(64)").HasMaxLength(64).IsRequired();
        row.Property(item => item.Quantity).HasColumnType("decimal(28,6)").HasPrecision(28, 6);
        row.Property(item => item.Version).HasColumnType("bigint");
    }
}

/// <summary>Describes the test-only migration model; no production domain entities are shared.</summary>
[DbContext(typeof(ProbeContext))]
public sealed class ProbeSnapshot : ModelSnapshot
{
    /// <summary>Records schema metadata, not tenant/run-specific runtime query filter values.</summary>
    protected override void BuildModel(ModelBuilder modelBuilder) => ProbeConstraints.BuildSchema(modelBuilder);
}
