using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class FileReferenceOwnerOrdinalIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Owner",
            table: "platform_file_references",
            type: "varchar(128)",
            maxLength: 128,
            nullable: false,
            collation: "utf8mb4_bin",
            oldClrType: typeof(string),
            oldType: "varchar(128)",
            oldMaxLength: 128);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Owner",
            table: "platform_file_references",
            type: "varchar(128)",
            maxLength: 128,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "varchar(128)",
            oldMaxLength: 128,
            oldCollation: "utf8mb4_bin");
    }
}
