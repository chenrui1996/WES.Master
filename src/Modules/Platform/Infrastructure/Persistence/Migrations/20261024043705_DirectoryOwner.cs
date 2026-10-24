using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Platform.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class DirectoryOwner : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_dictionary_entries",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Category = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                Code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                LanguageKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_dictionary_entries", x => new { x.TenantId, x.Id });
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "platform_directory_tenants",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                DefaultLanguage = table.Column<string>(type: "varchar(35)", maxLength: 35, nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_directory_tenants", x => x.TenantId);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "platform_dictionary_references",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                EntryId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                ReferenceId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_dictionary_references", x => new { x.TenantId, x.EntryId, x.ReferenceId });
                table.ForeignKey(
                    name: "FK_platform_dictionary_references_platform_dictionary_entries_T~",
                    columns: x => new { x.TenantId, x.EntryId },
                    principalTable: "platform_dictionary_entries",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "platform_dictionary_translations",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                EntryId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Language = table.Column<string>(type: "varchar(35)", maxLength: 35, nullable: false),
                Value = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_dictionary_translations", x => new { x.TenantId, x.EntryId, x.Language });
                table.ForeignKey(
                    name: "FK_platform_dictionary_translations_platform_dictionary_entries~",
                    columns: x => new { x.TenantId, x.EntryId },
                    principalTable: "platform_dictionary_entries",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "platform_departments",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Code = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_departments", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_platform_departments_platform_directory_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "platform_directory_tenants",
                    principalColumn: "TenantId",
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateTable(
            name: "platform_directory_users",
            columns: table => new
            {
                Id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                TenantId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Account = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false),
                AccountDigest = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                DepartmentId = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false),
                Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                Version = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_directory_users", x => new { x.TenantId, x.Id });
                table.ForeignKey(
                    name: "FK_platform_directory_users_platform_departments_TenantId_Depar~",
                    columns: x => new { x.TenantId, x.DepartmentId },
                    principalTable: "platform_departments",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            })
            .Annotation("MySQL:Charset", "utf8mb4");

        migrationBuilder.CreateIndex(
            name: "IX_platform_departments_TenantId_Code",
            table: "platform_departments",
            columns: new[] { "TenantId", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_platform_dictionary_entries_TenantId_Category_Code",
            table: "platform_dictionary_entries",
            columns: new[] { "TenantId", "Category", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_platform_directory_users_TenantId_AccountDigest",
            table: "platform_directory_users",
            columns: new[] { "TenantId", "AccountDigest" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_platform_directory_users_TenantId_DepartmentId",
            table: "platform_directory_users",
            columns: new[] { "TenantId", "DepartmentId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "platform_dictionary_references");

        migrationBuilder.DropTable(
            name: "platform_dictionary_translations");

        migrationBuilder.DropTable(
            name: "platform_directory_users");

        migrationBuilder.DropTable(
            name: "platform_dictionary_entries");

        migrationBuilder.DropTable(
            name: "platform_departments");

        migrationBuilder.DropTable(
            name: "platform_directory_tenants");
    }
}
