using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEngineBuilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EngineBuildId",
                table: "BackendConfigs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EngineBuildSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BuildsRootFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    GitHubApiToken = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineBuildSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LlamaCppBuildRecipes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    BackendType = table.Column<int>(type: "INTEGER", nullable: false),
                    GitRepoUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    GitRef = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CMakeArgs = table.Column<string>(type: "TEXT", nullable: false),
                    CMakeGenerator = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BuildConfig = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EnvironmentSetupCommand = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ExtraArtifactGlobs = table.Column<string>(type: "TEXT", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlamaCppBuildRecipes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LlamaCppBuilds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BackendType = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InstallPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    VersionTag = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CommitSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TargetOs = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TargetArch = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BuildCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlamaCppBuilds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlamaCppBuilds_LlamaCppBuildRecipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "LlamaCppBuildRecipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackendConfigs_EngineBuildId",
                table: "BackendConfigs",
                column: "EngineBuildId");

            migrationBuilder.CreateIndex(
                name: "IX_LlamaCppBuilds_InstallPath",
                table: "LlamaCppBuilds",
                column: "InstallPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LlamaCppBuilds_RecipeId",
                table: "LlamaCppBuilds",
                column: "RecipeId");

            migrationBuilder.AddForeignKey(
                name: "FK_BackendConfigs_LlamaCppBuilds_EngineBuildId",
                table: "BackendConfigs",
                column: "EngineBuildId",
                principalTable: "LlamaCppBuilds",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackendConfigs_LlamaCppBuilds_EngineBuildId",
                table: "BackendConfigs");

            migrationBuilder.DropTable(
                name: "EngineBuildSettings");

            migrationBuilder.DropTable(
                name: "LlamaCppBuilds");

            migrationBuilder.DropTable(
                name: "LlamaCppBuildRecipes");

            migrationBuilder.DropIndex(
                name: "IX_BackendConfigs_EngineBuildId",
                table: "BackendConfigs");

            migrationBuilder.DropColumn(
                name: "EngineBuildId",
                table: "BackendConfigs");
        }
    }
}
