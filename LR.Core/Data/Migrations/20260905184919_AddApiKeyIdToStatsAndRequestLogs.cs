using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyIdToStatsAndRequestLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ApiKeyId",
                table: "ModelStatistics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApiKeyId",
                table: "ApiRequestLogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelStatistics_ApiKeyId_Timestamp",
                table: "ModelStatistics",
                columns: new[] { "ApiKeyId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiRequestLogs_ApiKeyId",
                table: "ApiRequestLogs",
                column: "ApiKeyId");

            migrationBuilder.AddForeignKey(
                name: "FK_ApiRequestLogs_ApiKeys_ApiKeyId",
                table: "ApiRequestLogs",
                column: "ApiKeyId",
                principalTable: "ApiKeys",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ModelStatistics_ApiKeys_ApiKeyId",
                table: "ModelStatistics",
                column: "ApiKeyId",
                principalTable: "ApiKeys",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApiRequestLogs_ApiKeys_ApiKeyId",
                table: "ApiRequestLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_ModelStatistics_ApiKeys_ApiKeyId",
                table: "ModelStatistics");

            migrationBuilder.DropIndex(
                name: "IX_ModelStatistics_ApiKeyId_Timestamp",
                table: "ModelStatistics");

            migrationBuilder.DropIndex(
                name: "IX_ApiRequestLogs_ApiKeyId",
                table: "ApiRequestLogs");

            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "ModelStatistics");

            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "ApiRequestLogs");
        }
    }
}
