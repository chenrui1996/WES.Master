using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialSessions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Owner migrations must not alter shared database defaults; review generated operations before applying.
        migrationBuilder.CreateTable(
            name: "platform_sessions",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                UserId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Client = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                LastActivityAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_sessions", x => new { x.TenantId, x.Id });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "platform_refresh_tokens",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Digest = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                SessionId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ConsumedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_refresh_tokens", x => new { x.TenantId, x.Digest });
                table.ForeignKey(
                    name: "FK_platform_refresh_tokens_platform_sessions_TenantId_SessionId",
                    columns: x => new { x.TenantId, x.SessionId },
                    principalTable: "platform_sessions",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_platform_refresh_tokens_TenantId_SessionId",
            table: "platform_refresh_tokens",
            columns: new[] { "TenantId", "SessionId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_refresh_tokens");

        migrationBuilder.DropTable(
            name: "platform_sessions");
    }
}
