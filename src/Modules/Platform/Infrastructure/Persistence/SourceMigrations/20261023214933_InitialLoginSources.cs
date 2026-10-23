using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.SourceMigrations;

/// <inheritdoc />
public partial class InitialLoginSources : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_login_sources",
            columns: table => new
            {
                SourceDigest = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                LastObservedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                ObservationsData = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_login_sources", x => x.SourceDigest);
            })
            .Annotation("MySQL:Charset", "utf8mb4");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_login_sources");
    }
}
