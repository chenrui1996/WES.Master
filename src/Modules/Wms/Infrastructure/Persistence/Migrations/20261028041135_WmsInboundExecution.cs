using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WmsInboundExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wms_inbound_execution_operations",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OrderId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Input = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    ResultId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    AfterVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_inbound_execution_operations", x => new { x.TenantId, x.OperationId });
                    table.ForeignKey(
                        name: "FK_wms_inbound_execution_operations_wms_inbound_documents_Tenan~",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalTable: "wms_inbound_documents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wms_inbound_execution_operations_wms_inbound_facts_TenantId_~",
                        columns: x => new { x.TenantId, x.OperationId },
                        principalTable: "wms_inbound_facts",
                        principalColumns: new[] { "TenantId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_execution_operations_TenantId_Kind_ResultId",
                table: "wms_inbound_execution_operations",
                columns: new[] { "TenantId", "Kind", "ResultId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_execution_operations_TenantId_OrderId",
                table: "wms_inbound_execution_operations",
                columns: new[] { "TenantId", "OrderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wms_inbound_execution_operations");
        }
    }
}
