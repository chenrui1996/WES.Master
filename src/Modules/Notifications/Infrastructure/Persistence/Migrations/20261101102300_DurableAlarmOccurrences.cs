using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableAlarmOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ntf_alarm_occurrences",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    SourceEventId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    SourceOwner = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    SourceKind = table.Column<int>(type: "int", nullable: false),
                    ResourceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Summary = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "utf8mb4_bin"),
                    Fingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ntf_alarm_occurrences", x => new { x.TenantId, x.Id });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ntf_alarm_occurrences_TenantId_ReceivedAt_Id",
                table: "ntf_alarm_occurrences",
                columns: new[] { "TenantId", "ReceivedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ntf_alarm_occurrences_TenantId_SourceOwner_SourceEventId",
                table: "ntf_alarm_occurrences",
                columns: new[] { "TenantId", "SourceOwner", "SourceEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ntf_alarm_occurrences");
        }
    }
}
