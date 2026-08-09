using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoredResponses",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PreviousResponseId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", nullable: true),
                    OwnInputItemsJson = table.Column<string>(type: "TEXT", nullable: false),
                    OwnOutputItemsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    Store = table.Column<bool>(type: "INTEGER", nullable: false),
                    Background = table.Column<bool>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ToolChoiceJson = table.Column<string>(type: "TEXT", nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredResponses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredResponses_CreatedAt",
                table: "StoredResponses",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StoredResponses_PreviousResponseId",
                table: "StoredResponses",
                column: "PreviousResponseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoredResponses");
        }
    }
}
