using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModelFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Format",
                table: "LocalModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Existing rows are all files; the safetensors ones (diffusion parts) get their format.
            migrationBuilder.Sql(
                "UPDATE LocalModels SET Format = 1 WHERE lower(FilePath) LIKE '%.safetensors' OR lower(FilePath) LIKE '%.sft';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Format",
                table: "LocalModels");
        }
    }
}
