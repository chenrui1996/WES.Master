using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class DirectoryCommandReceipts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_directory_command_receipts",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Kind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                Fingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                ResultId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ResultVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_directory_command_receipts", x => new { x.TenantId, x.ActorId, x.OperationId });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_platform_directory_command_receipts_TenantId_CreatedAt",
            table: "platform_directory_command_receipts",
            columns: new[] { "TenantId", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_directory_command_receipts");
    }
}
