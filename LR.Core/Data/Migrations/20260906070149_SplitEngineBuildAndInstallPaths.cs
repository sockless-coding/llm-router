using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitEngineBuildAndInstallPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "BuildsRootFolder",
                table: "EngineBuildSettings",
                newName: "InstallRootFolder");

            migrationBuilder.AddColumn<string>(
                name: "BuildWorkspaceFolder",
                table: "EngineBuildSettings",
                type: "TEXT",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuildWorkspaceFolder",
                table: "EngineBuildSettings");

            migrationBuilder.RenameColumn(
                name: "InstallRootFolder",
                table: "EngineBuildSettings",
                newName: "BuildsRootFolder");
        }
    }
}
