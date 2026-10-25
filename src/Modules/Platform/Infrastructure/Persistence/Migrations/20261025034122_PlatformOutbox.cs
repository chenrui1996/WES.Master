using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class PlatformOutbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_outbox_messages",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                EventId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                SchemaVersion = table.Column<int>(type: "int", nullable: false),
                Owner = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                AggregateId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                AggregateVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CausationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                Payload = table.Column<string>(type: "longtext", nullable: false),
                Status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                Attempts = table.Column<int>(type: "int", nullable: false),
                NextAttemptAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                PublishedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                LastErrorCategory = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_outbox_messages", x => new { x.TenantId, x.EventId });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_platform_outbox_messages_TenantId_CreatedAt_EventId",
            table: "platform_outbox_messages",
            columns: new[] { "TenantId", "CreatedAt", "EventId" });

        migrationBuilder.CreateIndex(
            name: "IX_platform_outbox_messages_TenantId_Status_NextAttemptAt_Event~",
            table: "platform_outbox_messages",
            columns: new[] { "TenantId", "Status", "NextAttemptAt", "EventId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_outbox_messages");
    }
}
