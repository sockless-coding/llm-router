using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelPresets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModelPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ContextLength = table.Column<int>(type: "INTEGER", nullable: false),
                    GpuLayers = table.Column<int>(type: "INTEGER", nullable: false),
                    Flags = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelPresets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BackendType = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    IsHealthy = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActivePresetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerInstances_ModelPresets_ActivePresetId",
                        column: x => x.ActivePresetId,
                        principalTable: "ModelPresets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ModelStatistics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServerInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PresetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PromptTokensProcessed = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptProcessingMs = table.Column<double>(type: "REAL", nullable: false),
                    GeneratedTokenCount = table.Column<int>(type: "INTEGER", nullable: false),
                    GenerationMs = table.Column<double>(type: "REAL", nullable: false),
                    TotalLatencyMs = table.Column<double>(type: "REAL", nullable: false),
                    FirstTokenLatencyMs = table.Column<double>(type: "REAL", nullable: false),
                    ContextLengthUsed = table.Column<int>(type: "INTEGER", nullable: false),
                    ContextMaxLength = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelStatistics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelStatistics_ModelPresets_PresetId",
                        column: x => x.PresetId,
                        principalTable: "ModelPresets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ModelStatistics_ServerInstances_ServerInstanceId",
                        column: x => x.ServerInstanceId,
                        principalTable: "ServerInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoutingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PresetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BackendType = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetServerInstanceId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoutingRules_ServerInstances_TargetServerInstanceId",
                        column: x => x.TargetServerInstanceId,
                        principalTable: "ServerInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelPresets_ServerInstanceId",
                table: "ModelPresets",
                column: "ServerInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelStatistics_PresetId_Timestamp",
                table: "ModelStatistics",
                columns: new[] { "PresetId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelStatistics_ServerInstanceId_Timestamp",
                table: "ModelStatistics",
                columns: new[] { "ServerInstanceId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_RoutingRules_TargetServerInstanceId",
                table: "RoutingRules",
                column: "TargetServerInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerInstances_ActivePresetId",
                table: "ServerInstances",
                column: "ActivePresetId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerInstances_Name",
                table: "ServerInstances",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ModelPresets_ServerInstances_ServerInstanceId",
                table: "ModelPresets",
                column: "ServerInstanceId",
                principalTable: "ServerInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ModelPresets_ServerInstances_ServerInstanceId",
                table: "ModelPresets");

            migrationBuilder.DropTable(
                name: "ModelStatistics");

            migrationBuilder.DropTable(
                name: "RoutingRules");

            migrationBuilder.DropTable(
                name: "ServerInstances");

            migrationBuilder.DropTable(
                name: "ModelPresets");
        }
    }
}
