using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wes.Notifications.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BinaryAlarmOccurrenceColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This provider omits generic collation DDL and does not scaffold annotation-only column changes.
            migrationBuilder.Sql("""
                ALTER TABLE `ntf_alarm_occurrences`
                  MODIFY COLUMN `SourceOwner` varchar(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                  MODIFY COLUMN `Code` varchar(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                  MODIFY COLUMN `Summary` varchar(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
                  MODIFY COLUMN `Fingerprint` varchar(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE `ntf_alarm_occurrences`
                  MODIFY COLUMN `SourceOwner` varchar(64) NOT NULL,
                  MODIFY COLUMN `Code` varchar(64) NOT NULL,
                  MODIFY COLUMN `Summary` varchar(512) NOT NULL,
                  MODIFY COLUMN `Fingerprint` varchar(64) NOT NULL;
                """);
        }
    }
}
