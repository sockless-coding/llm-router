using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPresetServerSettingsAndModelLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiKeyFile",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiPrefix",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CacheIdleSlots",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CorsCredentials",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorsHeaders",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorsMethods",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorsOrigins",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CtxCheckpoints",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmbdNormalize",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Embeddings",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KvUnifiedPerSlot",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogColors",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LogDisable",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogFile",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LogJsonl",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LogPrefix",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogPromptsDir",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LogTimestamps",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LogVerbosity",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LoraInitWithoutApply",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaPath",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MetricsEndpoint",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MmprojId",
                table: "ModelPresets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelAlias",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelTags",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Numa",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OverrideKv",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Pooling",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PrefillAssistant",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PropsEndpoint",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Reranking",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReusePort",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RpcServers",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SkipChatParsing",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlotSavePath",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SlotsEndpoint",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpecDraftDevice",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpecDraftHfRepo",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SpecDraftModelId",
                table: "ModelPresets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SpmInfill",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SsePingInterval",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SslCertFile",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SslKeyFile",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SwaFull",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThreadsHttp",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoFfmpegDir",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "VideoFps",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VideoTimestampInterval",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Warmup",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WebUi",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelPresets_MmprojId",
                table: "ModelPresets",
                column: "MmprojId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelPresets_SpecDraftModelId",
                table: "ModelPresets",
                column: "SpecDraftModelId");

            migrationBuilder.AddForeignKey(
                name: "FK_ModelPresets_LocalModels_MmprojId",
                table: "ModelPresets",
                column: "MmprojId",
                principalTable: "LocalModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ModelPresets_LocalModels_SpecDraftModelId",
                table: "ModelPresets",
                column: "SpecDraftModelId",
                principalTable: "LocalModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ModelPresets_LocalModels_MmprojId",
                table: "ModelPresets");

            migrationBuilder.DropForeignKey(
                name: "FK_ModelPresets_LocalModels_SpecDraftModelId",
                table: "ModelPresets");

            migrationBuilder.DropIndex(
                name: "IX_ModelPresets_MmprojId",
                table: "ModelPresets");

            migrationBuilder.DropIndex(
                name: "IX_ModelPresets_SpecDraftModelId",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ApiKeyFile",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ApiPrefix",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CacheIdleSlots",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CorsCredentials",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CorsHeaders",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CorsMethods",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CorsOrigins",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CtxCheckpoints",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "EmbdNormalize",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Embeddings",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "KvUnifiedPerSlot",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LogColors",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LogDisable",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LogFile",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LogJsonl",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LogPrefix",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LogPromptsDir",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LogTimestamps",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LogVerbosity",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LoraInitWithoutApply",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MediaPath",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MetricsEndpoint",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MmprojId",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ModelAlias",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ModelTags",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Numa",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "OverrideKv",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Pooling",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "PrefillAssistant",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "PropsEndpoint",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Reranking",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ReusePort",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "RpcServers",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SkipChatParsing",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SlotSavePath",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SlotsEndpoint",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftDevice",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftHfRepo",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftModelId",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpmInfill",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SsePingInterval",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SslCertFile",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SslKeyFile",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SwaFull",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ThreadsHttp",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "VideoFfmpegDir",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "VideoFps",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "VideoTimestampInterval",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Warmup",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "WebUi",
                table: "ModelPresets");
        }
    }
}
