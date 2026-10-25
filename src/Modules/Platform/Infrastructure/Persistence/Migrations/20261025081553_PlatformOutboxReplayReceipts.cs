using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlatformOutboxReplayReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_outbox_replay_receipts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    EventId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Reason = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    PreviousAttempts = table.Column<int>(type: "int", nullable: false),
                    Fingerprint = table.Column<string>(type: "char(64)", maxLength: 64, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_outbox_replay_receipts", x => new { x.TenantId, x.OperationId });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_platform_outbox_replay_receipts_TenantId_EventId_RequestedAt",
                table: "platform_outbox_replay_receipts",
                columns: new[] { "TenantId", "EventId", "RequestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_outbox_replay_receipts");
        }
    }
}
