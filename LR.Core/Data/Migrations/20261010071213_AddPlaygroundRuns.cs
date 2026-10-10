using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaygroundRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaygroundRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<string>(type: "TEXT", nullable: false),
                    PresetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PresetName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ServerName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Engine = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelFile = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SettingsSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    SettingsHash = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    RequestParams = table.Column<string>(type: "TEXT", nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ColdStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    MessageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: true),
                    Response = table.Column<string>(type: "TEXT", nullable: true),
                    Reasoning = table.Column<string>(type: "TEXT", nullable: true),
                    FinishReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    TimeToFirstTokenMs = table.Column<double>(type: "REAL", nullable: true),
                    TotalMs = table.Column<double>(type: "REAL", nullable: true),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CachedTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptMs = table.Column<double>(type: "REAL", nullable: true),
                    PromptTokensPerSec = table.Column<double>(type: "REAL", nullable: true),
                    GenerationMs = table.Column<double>(type: "REAL", nullable: true),
                    GenTokensPerSec = table.Column<double>(type: "REAL", nullable: true),
                    DraftAccepted = table.Column<int>(type: "INTEGER", nullable: true),
                    DraftGenerated = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaygroundRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaygroundRuns_ModelPresets_PresetId",
                        column: x => x.PresetId,
                        principalTable: "ModelPresets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaygroundRuns_PresetId_Timestamp",
                table: "PlaygroundRuns",
                columns: new[] { "PresetId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaygroundRuns_Timestamp",
                table: "PlaygroundRuns",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaygroundRuns");
        }
    }
}
