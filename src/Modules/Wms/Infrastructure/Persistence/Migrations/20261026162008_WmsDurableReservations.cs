using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class WmsDurableReservations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "wms_stock_reservations",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ExecutionUnitId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                BalanceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                SourceDocumentId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                SourceLineId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                OriginalQuantity = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                RemainingQuantity = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                Kind = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wms_stock_reservations", x => new { x.TenantId, x.ExecutionUnitId });
                table.ForeignKey(
                    name: "FK_wms_stock_reservations_wms_stock_balances_TenantId_BalanceId",
                    columns: x => new { x.TenantId, x.BalanceId },
                    principalTable: "wms_stock_balances",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "wms_reservation_changes",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Action = table.Column<int>(type: "int", nullable: false),
                ExecutionUnitId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                BalanceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                SourceDocumentId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                SourceLineId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Quantity = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                Before = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                After = table.Column<decimal>(type: "decimal(28,6)", precision: 28, scale: 6, nullable: false),
                BeforeKind = table.Column<int>(type: "int", nullable: false),
                AfterKind = table.Column<int>(type: "int", nullable: false),
                OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wms_reservation_changes", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_wms_reservation_changes_wms_stock_reservations_TenantId_Exec~",
                    columns: x => new { x.TenantId, x.ExecutionUnitId },
                    principalTable: "wms_stock_reservations",
                    principalColumns: new[] { "TenantId", "ExecutionUnitId" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_wms_reservation_changes_TenantId_ExecutionUnitId",
            table: "wms_reservation_changes",
            columns: new[] { "TenantId", "ExecutionUnitId" });

        migrationBuilder.CreateIndex(
            name: "IX_wms_reservation_changes_TenantId_OperationId",
            table: "wms_reservation_changes",
            columns: new[] { "TenantId", "OperationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_wms_stock_reservations_TenantId_BalanceId",
            table: "wms_stock_reservations",
            columns: new[] { "TenantId", "BalanceId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "wms_reservation_changes");

        migrationBuilder.DropTable(
            name: "wms_stock_reservations");
    }
}
