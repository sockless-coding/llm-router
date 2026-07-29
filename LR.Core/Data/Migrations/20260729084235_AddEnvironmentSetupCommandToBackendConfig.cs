using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironmentSetupCommandToBackendConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvironmentSetupCommand",
                table: "BackendConfigs",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvironmentSetupCommand",
                table: "BackendConfigs");
        }
    }
}
