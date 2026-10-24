using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class DepartmentHierarchy : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ParentId",
            table: "platform_departments",
            type: "varchar(36)",
            maxLength: 36,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_platform_departments_TenantId_ParentId",
            table: "platform_departments",
            columns: new[] { "TenantId", "ParentId" });

        migrationBuilder.AddForeignKey(
            name: "FK_platform_departments_platform_departments_TenantId_ParentId",
            table: "platform_departments",
            columns: new[] { "TenantId", "ParentId" },
            principalTable: "platform_departments",
            principalColumns: new[] { "TenantId", "Id" },
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_platform_departments_platform_departments_TenantId_ParentId",
            table: "platform_departments");

        migrationBuilder.DropIndex(
            name: "IX_platform_departments_TenantId_ParentId",
            table: "platform_departments");

        migrationBuilder.DropColumn(
            name: "ParentId",
            table: "platform_departments");
    }
}
