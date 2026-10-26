using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlatformAuditQuerySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_audit_query_snapshots",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    SessionId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    AuthorizationFingerprint = table.Column<string>(type: "char(64)", maxLength: 64, nullable: false),
                    FilterFingerprint = table.Column<string>(type: "char(64)", maxLength: 64, nullable: false),
                    SortFingerprint = table.Column<string>(type: "char(64)", maxLength: 64, nullable: false),
                    From = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    To = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FilterAction = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    OrderedAuditIds = table.Column<string>(type: "longtext", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_audit_query_snapshots", x => new { x.TenantId, x.Id });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_platform_audit_query_snapshots_TenantId_ActorId_SessionId_Ex~",
                table: "platform_audit_query_snapshots",
                columns: new[] { "TenantId", "ActorId", "SessionId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_audit_query_snapshots");
        }
    }
}
