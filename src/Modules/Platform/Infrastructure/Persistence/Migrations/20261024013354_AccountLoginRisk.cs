using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AccountLoginRisk : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_login_accounts",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                AccountDigest = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                LastFailureAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                LockedUntil = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                FailuresData = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_login_accounts", x => new { x.TenantId, x.AccountDigest });
            })
            .Annotation("MySQL:Charset", "utf8mb4");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_login_accounts");
    }
}
