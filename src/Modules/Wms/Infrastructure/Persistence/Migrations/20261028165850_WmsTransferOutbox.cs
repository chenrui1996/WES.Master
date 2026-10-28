using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class WmsTransferOutbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "wms_transfer_outbox",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                PostingId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                Payload = table.Column<string>(type: "longtext", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_wms_transfer_outbox", x => new { x.TenantId, x.OperationId });
                table.ForeignKey(
                    name: "FK_wms_transfer_outbox_wms_stock_postings_TenantId_PostingId",
                    columns: x => new { x.TenantId, x.PostingId },
                    principalTable: "wms_stock_postings",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_wms_transfer_outbox_wms_transfer_facts_TenantId_OperationId",
                    columns: x => new { x.TenantId, x.OperationId },
                    principalTable: "wms_transfer_facts",
                    principalColumns: new[] { "TenantId", "OperationId" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_wms_transfer_outbox_TenantId_PostingId",
            table: "wms_transfer_outbox",
            columns: new[] { "TenantId", "PostingId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "wms_transfer_outbox");
    }
}
