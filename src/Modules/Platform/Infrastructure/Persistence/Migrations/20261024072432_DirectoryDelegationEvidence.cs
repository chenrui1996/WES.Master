using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class DirectoryDelegationEvidence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_directory_delegation_requirements",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ActorId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                OperationId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                RoleIdsData = table.Column<string>(type: "longtext", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_directory_delegation_requirements", x => new { x.TenantId, x.ActorId, x.OperationId });
                table.ForeignKey(
                    name: "FK_platform_directory_delegation_requirements_platform_director~",
                    columns: x => new { x.TenantId, x.ActorId, x.OperationId },
                    principalTable: "platform_directory_command_receipts",
                    principalColumns: new[] { "TenantId", "ActorId", "OperationId" },
                    onDelete: ReferentialAction.Cascade);
            })
            .Annotation("MySQL:Charset", "utf8mb4");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_directory_delegation_requirements");
    }
}
