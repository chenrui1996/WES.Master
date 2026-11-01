using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlarmConsole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ntf_alarm_audits",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    TargetId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Kind = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    Reason = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false, collation: "utf8mb4_bin"),
                    At = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ntf_alarm_audits", x => new { x.TenantId, x.Id });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ntf_alarm_console_locks",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ntf_alarm_console_locks", x => x.TenantId);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ntf_alarm_operations",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    TargetId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CreatorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Kind = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    Fingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    Body = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false, collation: "utf8mb4_bin")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ntf_alarm_operations", x => new { x.TenantId, x.OperationId });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ntf_alarms",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CreatorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    AggregateKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    SourceOwner = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    SourceKind = table.Column<int>(type: "int", nullable: false),
                    ResourceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    FirstOccurredAt = table.Column<long>(type: "bigint", nullable: false),
                    LastOccurredAt = table.Column<long>(type: "bigint", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    Version = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    AcknowledgedBy = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    AcknowledgedAt = table.Column<long>(type: "bigint", nullable: true),
                    SilenceUntil = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ntf_alarms", x => new { x.TenantId, x.Id });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ntf_alarm_deliveries",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    AlarmId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Channel = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    State = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    OccurrenceCount = table.Column<int>(type: "int", nullable: false),
                    DueAt = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "bigint", nullable: true),
                    ErrorCode = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "ascii_bin"),
                    Version = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ntf_alarm_deliveries", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ntf_alarm_deliveries_ntf_alarms_TenantId_AlarmId",
                        columns: x => new { x.TenantId, x.AlarmId },
                        principalTable: "ntf_alarms",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ntf_alarm_memberships",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OccurrenceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    AlarmId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ntf_alarm_memberships", x => new { x.TenantId, x.OccurrenceId });
                    table.ForeignKey(
                        name: "FK_ntf_alarm_memberships_ntf_alarm_occurrences_TenantId_Occurre~",
                        columns: x => new { x.TenantId, x.OccurrenceId },
                        principalTable: "ntf_alarm_occurrences",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ntf_alarm_memberships_ntf_alarms_TenantId_AlarmId",
                        columns: x => new { x.TenantId, x.AlarmId },
                        principalTable: "ntf_alarms",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ntf_alarm_delivery_attempts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    DeliveryId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    AttemptedAt = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    ErrorCode = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true, collation: "ascii_bin")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ntf_alarm_delivery_attempts", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_ntf_alarm_delivery_attempts_ntf_alarm_deliveries_TenantId_De~",
                        columns: x => new { x.TenantId, x.DeliveryId },
                        principalTable: "ntf_alarm_deliveries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ntf_alarm_audits_TenantId_OperationId",
                table: "ntf_alarm_audits",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ntf_alarm_deliveries_TenantId_AlarmId",
                table: "ntf_alarm_deliveries",
                columns: new[] { "TenantId", "AlarmId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ntf_alarm_deliveries_TenantId_State_DueAt_Id",
                table: "ntf_alarm_deliveries",
                columns: new[] { "TenantId", "State", "DueAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ntf_alarm_delivery_attempts_TenantId_DeliveryId",
                table: "ntf_alarm_delivery_attempts",
                columns: new[] { "TenantId", "DeliveryId" });

            migrationBuilder.CreateIndex(
                name: "IX_ntf_alarm_memberships_TenantId_AlarmId",
                table: "ntf_alarm_memberships",
                columns: new[] { "TenantId", "AlarmId" });

            migrationBuilder.CreateIndex(
                name: "IX_ntf_alarms_TenantId_AggregateKey",
                table: "ntf_alarms",
                columns: new[] { "TenantId", "AggregateKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ntf_alarms_TenantId_CreatorId_LastOccurredAt_Id",
                table: "ntf_alarms",
                columns: new[] { "TenantId", "CreatorId", "LastOccurredAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ntf_alarm_audits");

            migrationBuilder.DropTable(
                name: "ntf_alarm_console_locks");

            migrationBuilder.DropTable(
                name: "ntf_alarm_delivery_attempts");

            migrationBuilder.DropTable(
                name: "ntf_alarm_memberships");

            migrationBuilder.DropTable(
                name: "ntf_alarm_operations");

            migrationBuilder.DropTable(
                name: "ntf_alarm_deliveries");

            migrationBuilder.DropTable(
                name: "ntf_alarms");
        }
    }
}
