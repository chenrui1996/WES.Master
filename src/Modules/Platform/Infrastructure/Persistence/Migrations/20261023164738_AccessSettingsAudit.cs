using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AccessSettingsAudit : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_audit",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TargetId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Action = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                BeforeVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                AfterVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_audit", x => new { x.TenantId, x.Id });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "platform_roles",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Code = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                RulesData = table.Column<string>(type: "longtext", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_roles", x => new { x.TenantId, x.Id });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "platform_settings",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                Value = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Source = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_settings", x => new { x.TenantId, x.Id });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "platform_role_memberships",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                UserId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                RoleId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_role_memberships", x => new { x.TenantId, x.UserId, x.RoleId });
                table.ForeignKey(
                    name: "FK_platform_role_memberships_platform_roles_TenantId_RoleId",
                    columns: x => new { x.TenantId, x.RoleId },
                    principalTable: "platform_roles",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_platform_audit_TenantId_OccurredAt_Id",
            table: "platform_audit",
            columns: new[] { "TenantId", "OccurredAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_platform_role_memberships_TenantId_RoleId",
            table: "platform_role_memberships",
            columns: new[] { "TenantId", "RoleId" });

        migrationBuilder.CreateIndex(
            name: "IX_platform_roles_TenantId_Code",
            table: "platform_roles",
            columns: new[] { "TenantId", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_platform_settings_TenantId_Key",
            table: "platform_settings",
            columns: new[] { "TenantId", "Key" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_audit");

        migrationBuilder.DropTable(
            name: "platform_role_memberships");

        migrationBuilder.DropTable(
            name: "platform_settings");

        migrationBuilder.DropTable(
            name: "platform_roles");
    }
}
