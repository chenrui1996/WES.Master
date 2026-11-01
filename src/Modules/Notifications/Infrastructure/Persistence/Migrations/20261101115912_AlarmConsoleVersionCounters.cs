using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlarmConsoleVersionCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "Version",
                table: "ntf_alarms",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64,
                oldCollation: "ascii_bin");

            migrationBuilder.AlterColumn<long>(
                name: "Version",
                table: "ntf_alarm_deliveries",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64,
                oldCollation: "ascii_bin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Version",
                table: "ntf_alarms",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                collation: "ascii_bin",
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<string>(
                name: "Version",
                table: "ntf_alarm_deliveries",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                collation: "ascii_bin",
                oldClrType: typeof(long),
                oldType: "bigint");
        }
    }
}
