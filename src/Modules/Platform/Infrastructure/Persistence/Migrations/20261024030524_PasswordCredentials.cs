using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class PasswordCredentials : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_password_credentials",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                UserId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                NormalizedAccount = table.Column<string>(type: "longtext", nullable: false),
                AccountDigest = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ChangedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                HistoryData = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_password_credentials", x => new { x.TenantId, x.UserId });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_platform_password_credentials_TenantId_AccountDigest",
            table: "platform_password_credentials",
            columns: new[] { "TenantId", "AccountDigest" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_password_credentials");
    }
}
