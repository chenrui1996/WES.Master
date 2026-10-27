using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WmsMasterDataOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wms_outbox_messages",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    EventId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ContractName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Owner = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    AggregateId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    AggregateVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CausationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Payload = table.Column<string>(type: "longtext", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_outbox_messages", x => new { x.TenantId, x.EventId });
                    table.ForeignKey(
                        name: "FK_wms_outbox_messages_wms_master_data_changes_TenantId_EventId",
                        columns: x => new { x.TenantId, x.EventId },
                        principalTable: "wms_master_data_changes",
                        principalColumns: new[] { "TenantId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_wms_outbox_messages_TenantId_Status_OccurredAt_EventId",
                table: "wms_outbox_messages",
                columns: new[] { "TenantId", "Status", "OccurredAt", "EventId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wms_outbox_messages");
        }
    }
}
