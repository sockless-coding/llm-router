using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyUsageHourly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiKeyUsageHourly",
                columns: table => new
                {
                    HourStart = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApiKeyName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApiKeyPrefix = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    PresetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestCount = table.Column<long>(type: "INTEGER", nullable: false),
                    PromptTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    GeneratedTokens = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyUsageHourly", x => new { x.HourStart, x.ApiKeyId, x.ModelName });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyUsageHourly_ApiKeyId_HourStart",
                table: "ApiKeyUsageHourly",
                columns: new[] { "ApiKeyId", "HourStart" });

            // Backfill from the per-request ModelStatistics history so usage reports cover
            // everything recorded before this table existed. Its Timestamp is round-trip UTC
            // TEXT ("2026-11-14T10:23:45.1234567Z"), so the first 13 chars are the UTC hour.
            // Keys deleted before now were SetNull'd in ModelStatistics and land under "No key".
            migrationBuilder.Sql(@"
                INSERT INTO ApiKeyUsageHourly
                    (HourStart, ApiKeyId, ModelName, ApiKeyName, ApiKeyPrefix, PresetId, RequestCount, PromptTokens, GeneratedTokens)
                SELECT
                    substr(s.Timestamp, 1, 13) || ':00:00.0000000Z',
                    COALESCE(s.ApiKeyId, '00000000-0000-0000-0000-000000000000'),
                    substr(COALESCE(p.Name, '(unknown)'), 1, 128),
                    COALESCE(MAX(k.Name), CASE WHEN s.ApiKeyId IS NULL THEN 'No key' ELSE 'Deleted key' END),
                    MAX(k.KeyPrefix),
                    MAX(s.PresetId),
                    COUNT(*),
                    SUM(s.PromptTokensProcessed),
                    SUM(s.GeneratedTokenCount)
                FROM ModelStatistics s
                LEFT JOIN ModelPresets p ON p.Id = s.PresetId
                LEFT JOIN ApiKeys k ON k.Id = s.ApiKeyId
                WHERE s.Timestamp LIKE '____-__-__T__:%Z'
                GROUP BY 1, 2, 3;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeyUsageHourly");
        }
    }
}
