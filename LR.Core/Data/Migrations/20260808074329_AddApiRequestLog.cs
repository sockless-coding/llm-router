using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddApiRequestLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiRequestLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Protocol = table.Column<int>(type: "INTEGER", nullable: false),
                    EndpointPath = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ServerInstanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PresetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IncomingPayload = table.Column<string>(type: "TEXT", nullable: false),
                    TranslatedPayload = table.Column<string>(type: "TEXT", nullable: true),
                    BackendResponsePayload = table.Column<string>(type: "TEXT", nullable: true),
                    OutgoingPayloadSummary = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    IsStreaming = table.Column<bool>(type: "INTEGER", nullable: false),
                    WasQueued = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalLatencyMs = table.Column<double>(type: "REAL", nullable: true),
                    FirstTokenLatencyMs = table.Column<double>(type: "REAL", nullable: true),
                    PromptTokensProcessed = table.Column<int>(type: "INTEGER", nullable: false),
                    GeneratedTokenCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiRequestLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiRequestLogs_ModelPresets_PresetId",
                        column: x => x.PresetId,
                        principalTable: "ModelPresets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ApiRequestLogs_ServerInstances_ServerInstanceId",
                        column: x => x.ServerInstanceId,
                        principalTable: "ServerInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_PresetId",
                table: "ApiRequestLogs",
                column: "PresetId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_Protocol_Timestamp",
                table: "ApiRequestLogs",
                columns: new[] { "Protocol", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_ServerInstanceId",
                table: "ApiRequestLogs",
                column: "ServerInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_Timestamp",
                table: "ApiRequestLogs",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiRequestLogs");
        }
    }
}
