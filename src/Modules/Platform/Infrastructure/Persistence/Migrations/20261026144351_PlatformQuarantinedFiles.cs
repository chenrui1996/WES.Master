using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class PlatformQuarantinedFiles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_files",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CreatorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                FileName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                ContentType = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                ByteCount = table.Column<long>(type: "bigint", nullable: false),
                Sha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                State = table.Column<int>(type: "int", nullable: false),
                Stored = table.Column<bool>(type: "tinyint(1)", nullable: false),
                ScanAttemptId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                ScanStartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                LastScanVerdict = table.Column<int>(type: "int", nullable: true),
                ReferenceCount = table.Column<int>(type: "int", nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_files", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_platform_files_platform_directory_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "platform_directory_tenants",
                    principalColumn: "TenantId",
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "platform_file_references",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                FileId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Owner = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                EntityId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_file_references", x => new { x.TenantId, x.FileId, x.Owner, x.EntityId });
                table.ForeignKey(
                    name: "FK_platform_file_references_platform_files_TenantId_FileId",
                    columns: x => new { x.TenantId, x.FileId },
                    principalTable: "platform_files",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_platform_file_references_TenantId_Owner_EntityId",
            table: "platform_file_references",
            columns: new[] { "TenantId", "Owner", "EntityId" });

        migrationBuilder.CreateIndex(
            name: "IX_platform_files_TenantId_State_CreatedAt",
            table: "platform_files",
            columns: new[] { "TenantId", "State", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_file_references");

        migrationBuilder.DropTable(
            name: "platform_files");
    }
}
