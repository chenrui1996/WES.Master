using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class WmsMasterDataBinaryColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Oracle 10.0.7 omits CreateTable column collations; enforce the declared ordinal identity in actual DDL.
        migrationBuilder.Sql(
            "ALTER TABLE `wms_warehouses` MODIFY COLUMN `Code` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;");
        migrationBuilder.Sql(
            "ALTER TABLE `wms_locations` MODIFY COLUMN `Code` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, MODIFY COLUMN `SchemaVersion` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;");
        migrationBuilder.Sql(
            "ALTER TABLE `wms_master_data_changes` MODIFY COLUMN `Code` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, MODIFY COLUMN `SchemaVersion` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "ALTER TABLE `wms_warehouses` MODIFY COLUMN `Code` varchar(64) NOT NULL;");
        migrationBuilder.Sql(
            "ALTER TABLE `wms_locations` MODIFY COLUMN `Code` varchar(64) NOT NULL, MODIFY COLUMN `SchemaVersion` varchar(64) NOT NULL;");
        migrationBuilder.Sql(
            "ALTER TABLE `wms_master_data_changes` MODIFY COLUMN `Code` varchar(64) NOT NULL, MODIFY COLUMN `SchemaVersion` varchar(64) NULL;");
    }
}
