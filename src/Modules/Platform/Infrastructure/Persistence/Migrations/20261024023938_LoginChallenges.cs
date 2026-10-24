using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class LoginChallenges : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_login_challenges",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                AccountDigest = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                SourceDigest = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                AnswerMac = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                ConsumedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_login_challenges", x => new { x.TenantId, x.Id });
            })
            .Annotation("MySQL:Charset", "utf8mb4");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_login_challenges");
    }
}
