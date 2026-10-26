using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class FileUploadOperationIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "UploadOperationId",
            table: "platform_files",
            type: "varchar(36)",
            maxLength: 36,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_platform_files_TenantId_UploadOperationId",
            table: "platform_files",
            columns: new[] { "TenantId", "UploadOperationId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_platform_files_TenantId_UploadOperationId",
            table: "platform_files");

        migrationBuilder.DropColumn(
            name: "UploadOperationId",
            table: "platform_files");
    }
}
