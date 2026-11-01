using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlarmConsoleReceiptCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Body",
                table: "ntf_alarm_operations",
                type: "varchar(4096)",
                maxLength: 4096,
                nullable: false,
                collation: "utf8mb4_bin",
                oldClrType: typeof(string),
                oldType: "varchar(2048)",
                oldMaxLength: 2048,
                oldCollation: "utf8mb4_bin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Body",
                table: "ntf_alarm_operations",
                type: "varchar(2048)",
                maxLength: 2048,
                nullable: false,
                collation: "utf8mb4_bin",
                oldClrType: typeof(string),
                oldType: "varchar(4096)",
                oldMaxLength: 4096,
                oldCollation: "utf8mb4_bin");
        }
    }
}
