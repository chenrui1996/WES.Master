using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class WmsExactBatchIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Batch",
            table: "wms_stock_balances",
            type: "varchar(128)",
            maxLength: 128,
            nullable: false,
            collation: "utf8mb4_bin",
            oldClrType: typeof(string),
            oldType: "varchar(128)",
            oldMaxLength: 128);
        // Oracle 10.0.7 does not emit the required column-collation change from AlterColumn metadata alone.
        migrationBuilder.Sql(
            "ALTER TABLE `wms_stock_balances` MODIFY COLUMN `Batch` varchar(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Batch",
            table: "wms_stock_balances",
            type: "varchar(128)",
            maxLength: 128,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "varchar(128)",
            oldMaxLength: 128,
            oldCollation: "utf8mb4_bin");
        migrationBuilder.Sql(
            "ALTER TABLE `wms_stock_balances` MODIFY COLUMN `Batch` varchar(128) NOT NULL;");
    }
}
