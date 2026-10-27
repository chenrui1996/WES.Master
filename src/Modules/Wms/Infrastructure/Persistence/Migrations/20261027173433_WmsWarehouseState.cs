using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WmsWarehouseState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wms_warehouse_state_changes",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    WarehouseId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    BeforeVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    BeforeEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    AfterVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Reason = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_warehouse_state_changes", x => new { x.TenantId, x.OperationId });
                    table.ForeignKey(
                        name: "FK_wms_warehouse_state_changes_wms_warehouses_TenantId_Warehous~",
                        columns: x => new { x.TenantId, x.WarehouseId },
                        principalTable: "wms_warehouses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_wms_warehouse_state_changes_TenantId_WarehouseId_AfterVersion",
                table: "wms_warehouse_state_changes",
                columns: new[] { "TenantId", "WarehouseId", "AfterVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wms_warehouse_state_changes");
        }
    }
}
