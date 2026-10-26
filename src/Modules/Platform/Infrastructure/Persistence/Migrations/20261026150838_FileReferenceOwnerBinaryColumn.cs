using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class FileReferenceOwnerBinaryColumn : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Oracle 10.0.7 omits column-collation ALTERs from both supported metadata forms.
        migrationBuilder.Sql(
            "ALTER TABLE `platform_file_references` MODIFY COLUMN `Owner` varchar(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "ALTER TABLE `platform_file_references` MODIFY COLUMN `Owner` varchar(128) NOT NULL;");
    }
}
