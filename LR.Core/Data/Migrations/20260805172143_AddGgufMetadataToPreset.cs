using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddGgufMetadataToPreset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GgufArchitecture",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GgufChatTemplate",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GgufContextLength",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GgufEmbeddingLength",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GgufModelName",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GgufParameterSize",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GgufQuantizationLevel",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GgufRopeFreqBase",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GgufArchitecture",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "GgufChatTemplate",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "GgufContextLength",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "GgufEmbeddingLength",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "GgufModelName",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "GgufParameterSize",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "GgufQuantizationLevel",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "GgufRopeFreqBase",
                table: "ModelPresets");
        }
    }
}
