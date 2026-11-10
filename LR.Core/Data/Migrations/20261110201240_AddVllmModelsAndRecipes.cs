using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVllmModelsAndRecipes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VllmModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HfId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    ContextLength = table.Column<int>(type: "INTEGER", nullable: true),
                    MinVllmVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PipelineTag = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    HasSiteRecipe = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VllmModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VllmRecipes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VllmModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    Hardware = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Variant = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Strategy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Features = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ServedModel = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    Warnings = table.Column<string>(type: "TEXT", nullable: true),
                    VramGb = table.Column<double>(type: "REAL", nullable: true),
                    PageUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VllmRecipes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VllmRecipes_VllmModels_VllmModelId",
                        column: x => x.VllmModelId,
                        principalTable: "VllmModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VllmModels_HfId",
                table: "VllmModels",
                column: "HfId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VllmRecipes_VllmModelId",
                table: "VllmRecipes",
                column: "VllmModelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VllmRecipes");

            migrationBuilder.DropTable(
                name: "VllmModels");
        }
    }
}
