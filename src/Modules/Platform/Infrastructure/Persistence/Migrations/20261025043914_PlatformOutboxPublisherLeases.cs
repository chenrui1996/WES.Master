using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class PlatformOutboxPublisherLeases : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LeaseId",
            table: "platform_outbox_messages",
            type: "varchar(36)",
            maxLength: 36,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "LeaseUntil",
            table: "platform_outbox_messages",
            type: "datetime(6)",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_platform_outbox_messages_TenantId_Status_LeaseUntil_EventId",
            table: "platform_outbox_messages",
            columns: new[] { "TenantId", "Status", "LeaseUntil", "EventId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_platform_outbox_messages_TenantId_Status_LeaseUntil_EventId",
            table: "platform_outbox_messages");

        migrationBuilder.DropColumn(
            name: "LeaseId",
            table: "platform_outbox_messages");

        migrationBuilder.DropColumn(
            name: "LeaseUntil",
            table: "platform_outbox_messages");
    }
}
