using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WmsInboundReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wms_inbound_documents",
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
                    table.PrimaryKey("PK_wms_inbound_documents", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_wms_inbound_documents_wms_warehouses_TenantId_WarehouseId",
                        columns: x => new { x.TenantId, x.WarehouseId },
                        principalTable: "wms_warehouses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wms_inbound_facts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OrderId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Payload = table.Column<string>(type: "longtext", nullable: false),
                    ExecutionId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    PostingId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    AfterVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_inbound_facts", x => new { x.TenantId, x.OperationId });
                    table.ForeignKey(
                        name: "FK_wms_inbound_facts_wms_inbound_documents_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalTable: "wms_inbound_documents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wms_inbound_facts_wms_stock_postings_TenantId_PostingId",
                        columns: x => new { x.TenantId, x.PostingId },
                        principalTable: "wms_stock_postings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_documents_TenantId_Code",
                table: "wms_inbound_documents",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_documents_TenantId_CreationOperationId",
                table: "wms_inbound_documents",
                columns: new[] { "TenantId", "CreationOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_documents_TenantId_WarehouseId",
                table: "wms_inbound_documents",
                columns: new[] { "TenantId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_facts_TenantId_ExecutionId",
                table: "wms_inbound_facts",
                columns: new[] { "TenantId", "ExecutionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_facts_TenantId_OrderId_Sequence",
                table: "wms_inbound_facts",
                columns: new[] { "TenantId", "OrderId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_facts_TenantId_PostingId",
                table: "wms_inbound_facts",
                columns: new[] { "TenantId", "PostingId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wms_inbound_facts");

            migrationBuilder.DropTable(
                name: "wms_inbound_documents");
        }
    }
}
