using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class PlatformInboxReceipts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_inbox_receipts",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Consumer = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                EventId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ContractName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                SchemaVersion = table.Column<int>(type: "int", nullable: false),
                EnvelopeDigest = table.Column<string>(type: "char(64)", maxLength: 64, nullable: false),
                ProcessedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_inbox_receipts", x => new { x.TenantId, x.Consumer, x.EventId });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_platform_inbox_receipts_TenantId_ProcessedAt_Consumer",
            table: "platform_inbox_receipts",
            columns: new[] { "TenantId", "ProcessedAt", "Consumer" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_inbox_receipts");
    }
}
