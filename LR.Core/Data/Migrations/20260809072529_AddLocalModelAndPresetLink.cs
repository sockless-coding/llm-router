using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalModelAndPresetLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ModelId",
                table: "ModelPresets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LocalModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    HfRepoId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    HfFilename = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    HfRevision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Architecture = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    GgufModelName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ParameterSize = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    QuantizationLevel = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ContextLength = table.Column<int>(type: "INTEGER", nullable: true),
                    EmbeddingLength = table.Column<int>(type: "INTEGER", nullable: true),
                    FeedForwardLength = table.Column<int>(type: "INTEGER", nullable: true),
                    BlockCount = table.Column<int>(type: "INTEGER", nullable: true),
                    HeadCount = table.Column<int>(type: "INTEGER", nullable: true),
                    KvHeadCount = table.Column<int>(type: "INTEGER", nullable: true),
                    RopeFreqBase = table.Column<double>(type: "REAL", nullable: true),
                    EosTokenId = table.Column<int>(type: "INTEGER", nullable: true),
                    BosTokenId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChatTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    LicenseText = table.Column<string>(type: "TEXT", nullable: true),
                    AllKvPairsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalModels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelPresets_ModelId",
                table: "ModelPresets",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalModels_FilePath",
                table: "LocalModels",
                column: "FilePath",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ModelPresets_LocalModels_ModelId",
                table: "ModelPresets",
                column: "ModelId",
                principalTable: "LocalModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ModelPresets_LocalModels_ModelId",
                table: "ModelPresets");

            migrationBuilder.DropTable(
                name: "LocalModels");

            migrationBuilder.DropIndex(
                name: "IX_ModelPresets_ModelId",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "ModelPresets");
        }
    }
}
