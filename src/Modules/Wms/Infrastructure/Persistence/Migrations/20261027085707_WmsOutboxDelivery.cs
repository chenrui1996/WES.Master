using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WmsOutboxDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "wms_outbox_messages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryVersion",
                table: "wms_outbox_messages",
                type: "varchar(36)",
                maxLength: 36,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstFailureAt",
                table: "wms_outbox_messages",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastErrorCategory",
                table: "wms_outbox_messages",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseId",
                table: "wms_outbox_messages",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntil",
                table: "wms_outbox_messages",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "wms_outbox_messages",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1000, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "wms_outbox_messages",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.Sql("UPDATE wms_outbox_messages SET DeliveryVersion = EventId, NextAttemptAt = OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_wms_outbox_messages_TenantId_Status_NextAttemptAt_EventId",
                table: "wms_outbox_messages",
                columns: new[] { "TenantId", "Status", "NextAttemptAt", "EventId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("WMS delivery evidence must be retained. Use an approved forward migration.");
        }
    }
}
