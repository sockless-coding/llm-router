using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDraftAcceptanceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DraftAcceptanceRate",
                table: "ModelStatistics",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DraftAccepted",
                table: "ModelStatistics",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DraftGenerated",
                table: "ModelStatistics",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "DraftMeanLen",
                table: "ModelStatistics",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DraftAcceptanceRate",
                table: "ModelStatistics");

            migrationBuilder.DropColumn(
                name: "DraftAccepted",
                table: "ModelStatistics");

            migrationBuilder.DropColumn(
                name: "DraftGenerated",
                table: "ModelStatistics");

            migrationBuilder.DropColumn(
                name: "DraftMeanLen",
                table: "ModelStatistics");
        }
    }
}
