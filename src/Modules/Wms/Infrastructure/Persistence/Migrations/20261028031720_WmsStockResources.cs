using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WmsStockResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wms_stock_containers",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    WarehouseId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_stock_containers", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_wms_stock_containers_wms_warehouses_TenantId_WarehouseId",
                        columns: x => new { x.TenantId, x.WarehouseId },
                        principalTable: "wms_warehouses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wms_stock_units",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Countable = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Precision = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_stock_units", x => new { x.TenantId, x.Id });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wms_stock_items",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    BaseUnitId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    BatchRequired = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_stock_items", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_wms_stock_items_wms_stock_units_TenantId_BaseUnitId",
                        columns: x => new { x.TenantId, x.BaseUnitId },
                        principalTable: "wms_stock_units",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wms_stock_dimension_registrations",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    BalanceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    WarehouseId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    LocationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ItemId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    UnitId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ContainerId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Batch = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin"),
                    Precision = table.Column<int>(type: "int", nullable: false),
                    AfterVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_stock_dimension_registrations", x => new { x.TenantId, x.OperationId });
                    table.ForeignKey(
                        name: "FK_wms_stock_dimension_registrations_wms_locations_TenantId_Loc~",
                        columns: x => new { x.TenantId, x.LocationId },
                        principalTable: "wms_locations",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wms_stock_dimension_registrations_wms_stock_balances_TenantI~",
                        columns: x => new { x.TenantId, x.BalanceId },
                        principalTable: "wms_stock_balances",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wms_stock_dimension_registrations_wms_stock_containers_Tenan~",
                        columns: x => new { x.TenantId, x.ContainerId },
                        principalTable: "wms_stock_containers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wms_stock_dimension_registrations_wms_stock_items_TenantId_I~",
                        columns: x => new { x.TenantId, x.ItemId },
                        principalTable: "wms_stock_items",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wms_stock_dimension_registrations_wms_stock_units_TenantId_U~",
                        columns: x => new { x.TenantId, x.UnitId },
                        principalTable: "wms_stock_units",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wms_stock_dimension_registrations_wms_warehouses_TenantId_Wa~",
                        columns: x => new { x.TenantId, x.WarehouseId },
                        principalTable: "wms_warehouses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_containers_TenantId_Code",
                table: "wms_stock_containers",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_containers_TenantId_OperationId",
                table: "wms_stock_containers",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_containers_TenantId_WarehouseId",
                table: "wms_stock_containers",
                columns: new[] { "TenantId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_dimension_registrations_TenantId_BalanceId",
                table: "wms_stock_dimension_registrations",
                columns: new[] { "TenantId", "BalanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_dimension_registrations_TenantId_ContainerId",
                table: "wms_stock_dimension_registrations",
                columns: new[] { "TenantId", "ContainerId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_dimension_registrations_TenantId_ItemId",
                table: "wms_stock_dimension_registrations",
                columns: new[] { "TenantId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_dimension_registrations_TenantId_LocationId",
                table: "wms_stock_dimension_registrations",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_dimension_registrations_TenantId_UnitId",
                table: "wms_stock_dimension_registrations",
                columns: new[] { "TenantId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_dimension_registrations_TenantId_WarehouseId",
                table: "wms_stock_dimension_registrations",
                columns: new[] { "TenantId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_items_TenantId_BaseUnitId",
                table: "wms_stock_items",
                columns: new[] { "TenantId", "BaseUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_items_TenantId_Code",
                table: "wms_stock_items",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_items_TenantId_OperationId",
                table: "wms_stock_items",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_units_TenantId_Code",
                table: "wms_stock_units",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_stock_units_TenantId_OperationId",
                table: "wms_stock_units",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wms_stock_dimension_registrations");

            migrationBuilder.DropTable(
                name: "wms_stock_containers");

            migrationBuilder.DropTable(
                name: "wms_stock_items");

            migrationBuilder.DropTable(
                name: "wms_stock_units");
        }
    }
}
