using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "ServerInstances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "LocalModels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "EngineBuilds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    EnrollmentToken = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PublicUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteAgents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MachineId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Os = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Arch = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Rid = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OsDescription = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DataDirectory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastRemoteAddress = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SystemInfoJson = table.Column<string>(type: "TEXT", nullable: true),
                    HostOverride = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EngineRoot = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ModelRoot = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastConnectedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteAgents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServerInstances_AgentId",
                table: "ServerInstances",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalModels_AgentId",
                table: "LocalModels",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_EngineBuilds_AgentId",
                table: "EngineBuilds",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAgents_MachineId",
                table: "RemoteAgents",
                column: "MachineId");

            migrationBuilder.AddForeignKey(
                name: "FK_EngineBuilds_RemoteAgents_AgentId",
                table: "EngineBuilds",
                column: "AgentId",
                principalTable: "RemoteAgents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_LocalModels_RemoteAgents_AgentId",
                table: "LocalModels",
                column: "AgentId",
                principalTable: "RemoteAgents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ServerInstances_RemoteAgents_AgentId",
                table: "ServerInstances",
                column: "AgentId",
                principalTable: "RemoteAgents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EngineBuilds_RemoteAgents_AgentId",
                table: "EngineBuilds");

            migrationBuilder.DropForeignKey(
                name: "FK_LocalModels_RemoteAgents_AgentId",
                table: "LocalModels");

            migrationBuilder.DropForeignKey(
                name: "FK_ServerInstances_RemoteAgents_AgentId",
                table: "ServerInstances");

            migrationBuilder.DropTable(
                name: "AgentSettings");

            migrationBuilder.DropTable(
                name: "RemoteAgents");

            migrationBuilder.DropIndex(
                name: "IX_ServerInstances_AgentId",
                table: "ServerInstances");

            migrationBuilder.DropIndex(
                name: "IX_LocalModels_AgentId",
                table: "LocalModels");

            migrationBuilder.DropIndex(
                name: "IX_EngineBuilds_AgentId",
                table: "EngineBuilds");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "ServerInstances");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "LocalModels");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "EngineBuilds");
        }
    }
}
