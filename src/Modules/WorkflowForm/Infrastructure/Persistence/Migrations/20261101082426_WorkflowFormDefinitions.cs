using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.WorkflowForm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowFormDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wf_audit_facts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ResourceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ResourceKind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, collation: "utf8mb4_bin"),
                    Action = table.Column<string>(type: "varchar(48)", maxLength: 48, nullable: false, collation: "utf8mb4_bin"),
                    BeforeVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    AfterVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wf_audit_facts", x => new { x.TenantId, x.Id });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wf_operation_receipts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Action = table.Column<string>(type: "varchar(48)", maxLength: 48, nullable: false, collation: "utf8mb4_bin"),
                    ResourceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Fingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    AuditFactId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    ResultJson = table.Column<string>(type: "longtext", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wf_operation_receipts", x => new { x.TenantId, x.OperationId });
                    table.ForeignKey(
                        name: "FK_wf_operation_receipts_wf_audit_facts_TenantId_AuditFactId",
                        columns: x => new { x.TenantId, x.AuditFactId },
                        principalTable: "wf_audit_facts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wf_definition_drafts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    BusinessType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    PlannedVersion = table.Column<int>(type: "int", nullable: false),
                    DraftVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    SnapshotJson = table.Column<string>(type: "longtext", nullable: false),
                    LastOperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    AuditFactId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wf_definition_drafts", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_wf_definition_drafts_wf_audit_facts_TenantId_AuditFactId",
                        columns: x => new { x.TenantId, x.AuditFactId },
                        principalTable: "wf_audit_facts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wf_definition_drafts_wf_operation_receipts_TenantId_LastOper~",
                        columns: x => new { x.TenantId, x.LastOperationId },
                        principalTable: "wf_operation_receipts",
                        principalColumns: new[] { "TenantId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wf_definition_versions",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    VersionToken = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    BusinessType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    SchemaId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    SnapshotJson = table.Column<string>(type: "longtext", nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wf_definition_versions", x => new { x.TenantId, x.Id, x.Version });
                    table.ForeignKey(
                        name: "FK_wf_definition_versions_wf_operation_receipts_TenantId_Operat~",
                        columns: x => new { x.TenantId, x.OperationId },
                        principalTable: "wf_operation_receipts",
                        principalColumns: new[] { "TenantId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wf_schema_drafts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    BusinessType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    PlannedVersion = table.Column<int>(type: "int", nullable: false),
                    DraftVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    SnapshotJson = table.Column<string>(type: "longtext", nullable: false),
                    LastOperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    AuditFactId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wf_schema_drafts", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_wf_schema_drafts_wf_audit_facts_TenantId_AuditFactId",
                        columns: x => new { x.TenantId, x.AuditFactId },
                        principalTable: "wf_audit_facts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wf_schema_drafts_wf_operation_receipts_TenantId_LastOperatio~",
                        columns: x => new { x.TenantId, x.LastOperationId },
                        principalTable: "wf_operation_receipts",
                        principalColumns: new[] { "TenantId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wf_schema_versions",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    VersionToken = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    BusinessType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin"),
                    SnapshotJson = table.Column<string>(type: "longtext", nullable: false),
                    OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wf_schema_versions", x => new { x.TenantId, x.Id, x.Version });
                    table.ForeignKey(
                        name: "FK_wf_schema_versions_wf_operation_receipts_TenantId_OperationId",
                        columns: x => new { x.TenantId, x.OperationId },
                        principalTable: "wf_operation_receipts",
                        principalColumns: new[] { "TenantId", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_wf_audit_facts_TenantId_OperationId",
                table: "wf_audit_facts",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wf_definition_drafts_TenantId_AuditFactId",
                table: "wf_definition_drafts",
                columns: new[] { "TenantId", "AuditFactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wf_definition_drafts_TenantId_LastOperationId",
                table: "wf_definition_drafts",
                columns: new[] { "TenantId", "LastOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wf_definition_versions_TenantId_OperationId",
                table: "wf_definition_versions",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wf_operation_receipts_TenantId_AuditFactId",
                table: "wf_operation_receipts",
                columns: new[] { "TenantId", "AuditFactId" });

            migrationBuilder.CreateIndex(
                name: "IX_wf_schema_drafts_TenantId_AuditFactId",
                table: "wf_schema_drafts",
                columns: new[] { "TenantId", "AuditFactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wf_schema_drafts_TenantId_LastOperationId",
                table: "wf_schema_drafts",
                columns: new[] { "TenantId", "LastOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wf_schema_versions_TenantId_OperationId",
                table: "wf_schema_versions",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wf_definition_drafts");

            migrationBuilder.DropTable(
                name: "wf_definition_versions");

            migrationBuilder.DropTable(
                name: "wf_schema_drafts");

            migrationBuilder.DropTable(
                name: "wf_schema_versions");

            migrationBuilder.DropTable(
                name: "wf_operation_receipts");

            migrationBuilder.DropTable(
                name: "wf_audit_facts");
        }
    }
}
