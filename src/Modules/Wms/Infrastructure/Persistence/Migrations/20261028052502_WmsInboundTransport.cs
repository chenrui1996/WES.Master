using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WmsInboundTransport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wms_inbound_transport_intents",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    EventId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OrderId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ExecutionId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ReceiptOperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    SourceBalanceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Envelope = table.Column<string>(type: "longtext", nullable: false),
                    TransportId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    AssociationOperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_inbound_transport_intents", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_wms_inbound_transport_intents_wms_inbound_documents_TenantId~",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalTable: "wms_inbound_documents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wms_inbound_transport_intents_wms_inbound_facts_TenantId_Rec~",
                        columns: x => new { x.TenantId, x.ReceiptOperationId },
                        principalTable: "wms_inbound_facts",
                        principalColumns: new[] { "TenantId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wms_inbound_transport_intents_wms_stock_balances_TenantId_So~",
                        columns: x => new { x.TenantId, x.SourceBalanceId },
                        principalTable: "wms_stock_balances",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wms_inbound_transport_inbox",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    EventId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CompletionId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    IntentId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ExecutionId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Body = table.Column<string>(type: "longtext", nullable: false),
                    PostingId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    Applied = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wms_inbound_transport_inbox", x => new { x.TenantId, x.EventId });
                    table.ForeignKey(
                        name: "FK_wms_inbound_transport_inbox_wms_inbound_transport_intents_Te~",
                        columns: x => new { x.TenantId, x.IntentId },
                        principalTable: "wms_inbound_transport_intents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wms_inbound_transport_inbox_wms_stock_postings_TenantId_Post~",
                        columns: x => new { x.TenantId, x.PostingId },
                        principalTable: "wms_stock_postings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_transport_inbox_TenantId_CompletionId",
                table: "wms_inbound_transport_inbox",
                columns: new[] { "TenantId", "CompletionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_transport_inbox_TenantId_ExecutionId",
                table: "wms_inbound_transport_inbox",
                columns: new[] { "TenantId", "ExecutionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_transport_inbox_TenantId_IntentId",
                table: "wms_inbound_transport_inbox",
                columns: new[] { "TenantId", "IntentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_transport_inbox_TenantId_PostingId",
                table: "wms_inbound_transport_inbox",
                columns: new[] { "TenantId", "PostingId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_transport_intents_TenantId_EventId",
                table: "wms_inbound_transport_intents",
                columns: new[] { "TenantId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_transport_intents_TenantId_ExecutionId",
                table: "wms_inbound_transport_intents",
                columns: new[] { "TenantId", "ExecutionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_transport_intents_TenantId_OrderId",
                table: "wms_inbound_transport_intents",
                columns: new[] { "TenantId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_transport_intents_TenantId_ReceiptOperationId",
                table: "wms_inbound_transport_intents",
                columns: new[] { "TenantId", "ReceiptOperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_transport_intents_TenantId_SourceBalanceId",
                table: "wms_inbound_transport_intents",
                columns: new[] { "TenantId", "SourceBalanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_wms_inbound_transport_intents_TenantId_TransportId",
                table: "wms_inbound_transport_intents",
                columns: new[] { "TenantId", "TransportId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wms_inbound_transport_inbox");

            migrationBuilder.DropTable(
                name: "wms_inbound_transport_intents");
        }
    }
}
