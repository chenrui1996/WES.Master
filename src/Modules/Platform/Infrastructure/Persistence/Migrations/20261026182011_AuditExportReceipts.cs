using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditExportReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_audit_export_receipts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    SessionId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    SnapshotId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Columns = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    AuditId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_audit_export_receipts", x => new { x.TenantId, x.OperationId });
                    table.ForeignKey(
                        name: "FK_platform_audit_export_receipts_platform_audit_TenantId_Audit~",
                        columns: x => new { x.TenantId, x.AuditId },
                        principalTable: "platform_audit",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_platform_audit_export_receipts_TenantId_AuditId",
                table: "platform_audit_export_receipts",
                columns: new[] { "TenantId", "AuditId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_audit_export_receipts");
        }
    }
}
