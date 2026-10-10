using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameLlamaCppExecutableFolderPathToInstallFolderPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LlamaCppExecutableFolderPath",
                table: "BackendConfigs",
                newName: "InstallFolderPath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "InstallFolderPath",
                table: "BackendConfigs",
                newName: "LlamaCppExecutableFolderPath");
        }
    }
}
