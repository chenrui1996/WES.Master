using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class WmsTransferOrders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "wms_transfer_documents",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CreationOperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CreatorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                WarehouseId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                Plan = table.Column<string>(type: "longtext", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                State = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                Sequence = table.Column<int>(type: "int", nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wms_transfer_documents", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_wms_transfer_documents_wms_warehouses_TenantId_WarehouseId",
                    columns: x => new { x.TenantId, x.WarehouseId },
                    principalTable: "wms_warehouses",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "wms_transfer_facts",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                OrderId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Kind = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false),
                LineId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Input = table.Column<string>(type: "longtext", nullable: false),
                Sequence = table.Column<int>(type: "int", nullable: false),
                AfterVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                State = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wms_transfer_facts", x => new { x.TenantId, x.OperationId });
                table.ForeignKey(
                    name: "FK_wms_transfer_facts_wms_transfer_documents_TenantId_OrderId",
                    columns: x => new { x.TenantId, x.OrderId },
                    principalTable: "wms_transfer_documents",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_wms_transfer_documents_TenantId_Code",
            table: "wms_transfer_documents",
            columns: new[] { "TenantId", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_wms_transfer_documents_TenantId_CreationOperationId",
            table: "wms_transfer_documents",
            columns: new[] { "TenantId", "CreationOperationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_wms_transfer_documents_TenantId_WarehouseId",
            table: "wms_transfer_documents",
            columns: new[] { "TenantId", "WarehouseId" });

        migrationBuilder.CreateIndex(
            name: "IX_wms_transfer_facts_TenantId_OrderId_LineId_Kind",
            table: "wms_transfer_facts",
            columns: new[] { "TenantId", "OrderId", "LineId", "Kind" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_wms_transfer_facts_TenantId_OrderId_Sequence",
            table: "wms_transfer_facts",
            columns: new[] { "TenantId", "OrderId", "Sequence" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "wms_transfer_facts");

        migrationBuilder.DropTable(
            name: "wms_transfer_documents");
    }
}
