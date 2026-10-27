using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class BusinessFileBindingOperations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_file_binding_operations",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                FileId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Owner = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin"),
                EntityId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Attached = table.Column<bool>(type: "tinyint(1)", nullable: false),
                ExpectedVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ResultVersion = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                AuditId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CorrelationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_file_binding_operations", x => new { x.TenantId, x.OperationId });
                table.ForeignKey(
                    name: "FK_platform_file_binding_operations_platform_files_TenantId_Fil~",
                    columns: x => new { x.TenantId, x.FileId },
                    principalTable: "platform_files",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_platform_file_binding_operations_TenantId_FileId_CreatedAt",
            table: "platform_file_binding_operations",
            columns: new[] { "TenantId", "FileId", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_file_binding_operations");
    }
}
