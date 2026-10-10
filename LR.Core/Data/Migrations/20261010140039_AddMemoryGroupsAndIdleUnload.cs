using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryGroupsAndIdleUnload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IdleUnloadMinutes",
                table: "ServerInstances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MemoryGroup",
                table: "ServerInstances",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MemoryEstimateMb",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MemoryGroups",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BudgetMb = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryGroups", x => x.Name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryGroups");

            migrationBuilder.DropColumn(
                name: "IdleUnloadMinutes",
                table: "ServerInstances");

            migrationBuilder.DropColumn(
                name: "MemoryGroup",
                table: "ServerInstances");

            migrationBuilder.DropColumn(
                name: "MemoryEstimateMb",
                table: "ModelPresets");
        }
    }
}
