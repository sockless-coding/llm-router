using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeApiRequestLogTimestampToRoundTripUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ApiRequestLog.Timestamp now uses the same DateTimeOffset → round-trip UTC TEXT
            // converter as ModelStatistics ("O" format, e.g. 2026-09-09T17:55:07.4651876Z).
            // Existing rows were written by the SQLite provider's default DateTimeOffset
            // format ("2026-09-09 17:55:07.4651876+00:00"), which neither sorts nor compares
            // correctly against the new format. Rewrite them in place. The guard makes this
            // idempotent and leaves any already-normalised rows untouched.
            migrationBuilder.Sql(@"
                UPDATE ApiRequestLogs
                SET Timestamp = replace(substr(Timestamp, 1, instr(Timestamp, '+') - 1), ' ', 'T') || 'Z'
                WHERE instr(Timestamp, '+') > 0 AND Timestamp LIKE '% %';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: 2026-09-09T17:55:07.4651876Z -> 2026-09-09 17:55:07.4651876+00:00
            migrationBuilder.Sql(@"
                UPDATE ApiRequestLogs
                SET Timestamp = replace(rtrim(Timestamp, 'Z'), 'T', ' ') || '+00:00'
                WHERE Timestamp LIKE '____-__-__T%Z';");
        }
    }
}
