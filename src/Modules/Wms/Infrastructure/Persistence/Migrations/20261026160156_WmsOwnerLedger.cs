using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class WmsOwnerLedger : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "wms_stock_balances",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                WarehouseId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                LocationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ItemId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Batch = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                ContainerId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                UnitId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Precision = table.Column<int>(type: "int", nullable: false),
                Quantity = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wms_stock_balances", x => new { x.TenantId, x.Id });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "wms_stock_postings",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Kind = table.Column<int>(type: "int", nullable: false),
                SourceDocumentId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                SourceLineId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ExecutionUnitId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                SourceBalanceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                TargetBalanceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                Quantity = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wms_stock_postings", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_wms_stock_postings_wms_stock_balances_TenantId_SourceBalance~",
                    columns: x => new { x.TenantId, x.SourceBalanceId },
                    principalTable: "wms_stock_balances",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_wms_stock_postings_wms_stock_balances_TenantId_TargetBalance~",
                    columns: x => new { x.TenantId, x.TargetBalanceId },
                    principalTable: "wms_stock_balances",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "wms_stock_entries",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                PostingId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                BalanceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Quantity = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                Before = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                After = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wms_stock_entries", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_wms_stock_entries_wms_stock_balances_TenantId_BalanceId",
                    columns: x => new { x.TenantId, x.BalanceId },
                    principalTable: "wms_stock_balances",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_wms_stock_entries_wms_stock_postings_TenantId_PostingId",
                    columns: x => new { x.TenantId, x.PostingId },
                    principalTable: "wms_stock_postings",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_wms_stock_balances_TenantId_WarehouseId_LocationId_ItemId_Ba~",
            table: "wms_stock_balances",
            columns: new[] { "TenantId", "WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId", "UnitId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_wms_stock_entries_TenantId_BalanceId",
            table: "wms_stock_entries",
            columns: new[] { "TenantId", "BalanceId" });

        migrationBuilder.CreateIndex(
            name: "IX_wms_stock_entries_TenantId_OccurredAt_Id",
            table: "wms_stock_entries",
            columns: new[] { "TenantId", "OccurredAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_wms_stock_entries_TenantId_PostingId_BalanceId",
            table: "wms_stock_entries",
            columns: new[] { "TenantId", "PostingId", "BalanceId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_wms_stock_postings_TenantId_Kind_ExecutionUnitId",
            table: "wms_stock_postings",
            columns: new[] { "TenantId", "Kind", "ExecutionUnitId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_wms_stock_postings_TenantId_OperationId",
            table: "wms_stock_postings",
            columns: new[] { "TenantId", "OperationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_wms_stock_postings_TenantId_SourceBalanceId",
            table: "wms_stock_postings",
            columns: new[] { "TenantId", "SourceBalanceId" });

        migrationBuilder.CreateIndex(
            name: "IX_wms_stock_postings_TenantId_TargetBalanceId",
            table: "wms_stock_postings",
            columns: new[] { "TenantId", "TargetBalanceId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "wms_stock_entries");

        migrationBuilder.DropTable(
            name: "wms_stock_postings");

        migrationBuilder.DropTable(
            name: "wms_stock_balances");
    }
}
