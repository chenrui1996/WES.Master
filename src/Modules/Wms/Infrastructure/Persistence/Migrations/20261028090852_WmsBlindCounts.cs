using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WmsBlindCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wms_stock_counts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CreatorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    WarehouseId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    LocationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    LockOwnerId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    SnapshotAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LinesJson = table.Column<string>(type: "longtext", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_stock_counts", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_wms_stock_counts_wms_locations_TenantId_LocationId",
                        columns: x => new { x.TenantId, x.LocationId },
                        principalTable: "wms_locations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wms_stock_count_facts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    PlanId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Input = table.Column<string>(type: "longtext", nullable: false),
                    AfterVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    LinesJson = table.Column<string>(type: "longtext", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_stock_count_facts", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_wms_stock_count_facts_wms_stock_counts_TenantId_PlanId",
                        columns: x => new { x.TenantId, x.PlanId },
                        principalTable: "wms_stock_counts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_count_facts_TenantId_OperationId",
                table: "wms_stock_count_facts",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_count_facts_TenantId_PlanId_Sequence",
                table: "wms_stock_count_facts",
                columns: new[] { "TenantId", "PlanId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_counts_TenantId_Code",
                table: "wms_stock_counts",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_counts_TenantId_LocationId",
                table: "wms_stock_counts",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_counts_TenantId_WarehouseId_LocationId_State",
                table: "wms_stock_counts",
                columns: new[] { "TenantId", "WarehouseId", "LocationId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wms_stock_count_facts");

            migrationBuilder.DropTable(
                name: "wms_stock_counts");
        }
    }
}
