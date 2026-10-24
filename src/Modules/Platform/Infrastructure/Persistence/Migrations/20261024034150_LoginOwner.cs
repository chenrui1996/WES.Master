using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class LoginOwner : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "RequiresPasswordChange",
            table: "platform_sessions",
            type: "tinyint(1)",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "Enabled",
            table: "platform_password_credentials",
            type: "tinyint(1)",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "platform_login_events",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                AccountDigest = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                SourceDigest = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Kind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                SessionId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_login_events", x => new { x.TenantId, x.Id });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_platform_login_events_TenantId_OccurredAt_Id",
            table: "platform_login_events",
            columns: new[] { "TenantId", "OccurredAt", "Id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_login_events");

        migrationBuilder.DropColumn(
            name: "RequiresPasswordChange",
            table: "platform_sessions");

        migrationBuilder.DropColumn(
            name: "Enabled",
            table: "platform_password_credentials");
    }
}
