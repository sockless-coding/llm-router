using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <summary>
    /// LlamaCppBuilds becomes EngineBuilds with an Engine column (existing rows are llama.cpp = 0).
    /// Hand-written as a rename: the scaffolded drop-and-recreate would have lost every tracked build.
    /// </summary>
    public partial class GeneralizeEngineBuildsAcrossEngines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackendConfigs_LlamaCppBuilds_EngineBuildId",
                table: "BackendConfigs");

            migrationBuilder.DropForeignKey(
                name: "FK_LlamaCppBuilds_LlamaCppBuildRecipes_RecipeId",
                table: "LlamaCppBuilds");

            migrationBuilder.RenameTable(
                name: "LlamaCppBuilds",
                newName: "EngineBuilds");

            migrationBuilder.RenameIndex(
                name: "IX_LlamaCppBuilds_RecipeId",
                table: "EngineBuilds",
                newName: "IX_EngineBuilds_RecipeId");

            migrationBuilder.RenameIndex(
                name: "IX_LlamaCppBuilds_InstallPath",
                table: "EngineBuilds",
                newName: "IX_EngineBuilds_InstallPath");

            migrationBuilder.AddColumn<int>(
                name: "Engine",
                table: "EngineBuilds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddForeignKey(
                name: "FK_EngineBuilds_LlamaCppBuildRecipes_RecipeId",
                table: "EngineBuilds",
                column: "RecipeId",
                principalTable: "LlamaCppBuildRecipes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_BackendConfigs_EngineBuilds_EngineBuildId",
                table: "BackendConfigs",
                column: "EngineBuildId",
                principalTable: "EngineBuilds",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackendConfigs_EngineBuilds_EngineBuildId",
                table: "BackendConfigs");

            migrationBuilder.DropForeignKey(
                name: "FK_EngineBuilds_LlamaCppBuildRecipes_RecipeId",
                table: "EngineBuilds");

            migrationBuilder.DropColumn(
                name: "Engine",
                table: "EngineBuilds");

            migrationBuilder.RenameTable(
                name: "EngineBuilds",
                newName: "LlamaCppBuilds");

            migrationBuilder.RenameIndex(
                name: "IX_EngineBuilds_RecipeId",
                table: "LlamaCppBuilds",
                newName: "IX_LlamaCppBuilds_RecipeId");

            migrationBuilder.RenameIndex(
                name: "IX_EngineBuilds_InstallPath",
                table: "LlamaCppBuilds",
                newName: "IX_LlamaCppBuilds_InstallPath");

            migrationBuilder.AddForeignKey(
                name: "FK_LlamaCppBuilds_LlamaCppBuildRecipes_RecipeId",
                table: "LlamaCppBuilds",
                column: "RecipeId",
                principalTable: "LlamaCppBuildRecipes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_BackendConfigs_LlamaCppBuilds_EngineBuildId",
                table: "BackendConfigs",
                column: "EngineBuildId",
                principalTable: "LlamaCppBuilds",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
