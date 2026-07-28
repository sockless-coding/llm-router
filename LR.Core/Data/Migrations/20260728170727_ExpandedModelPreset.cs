using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Migrations
{
    /// <inheritdoc />
    public partial class ExpandedModelPreset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextLength",
                table: "ModelPresets");

            migrationBuilder.AlterColumn<int>(
                name: "GpuLayers",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "ApiKey",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BatchSize",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CachePrompt",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CacheRam",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CacheTypeK",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CacheTypeV",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChatTemplate",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContBatching",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextSize",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Device",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DryAllowedLength",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "DryBase",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "DryMultiplier",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DryPenaltyLastN",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "DynatempExp",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "DynatempRange",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fit",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FlashAttention",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "FrequencyPenalty",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Host",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IgnoreEos",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageMaxTokens",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageMinTokens",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Jinja",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KeepN",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KvOffload",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoadMode",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lora",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MainGpu",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "MinP",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Mirostat",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "MirostatEta",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "MirostatTau",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mmproj",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Parallel",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Port",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PredictN",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "PresencePenalty",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reasoning",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReasoningBudget",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Repack",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RepeatLastN",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "RepeatPenalty",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "RopeFreqBase",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "RopeFreqScale",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "RopeScale",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RopeScalingType",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Seed",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpecDraftGpuLayers",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpecDraftModel",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpecDraftNMax",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpecDraftNMin",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "SpecDraftPMin",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpecDraftThreads",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpecDraftTypeK",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpecDraftTypeV",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpecType",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SplitMode",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "Temperature",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TensorSplit",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Threads",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThreadsBatch",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Timeout",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TopK",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TopNSigma",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TopP",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TypicalP",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UbatchSize",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "XtcProbability",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "XtcThreshold",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "YarnAttnFactor",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "YarnBetaFast",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "YarnBetaSlow",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "YarnExtFactor",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "YarnOrigCtx",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiKey",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "BatchSize",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CachePrompt",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CacheRam",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CacheTypeK",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CacheTypeV",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ChatTemplate",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ContBatching",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ContextSize",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Device",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "DryAllowedLength",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "DryBase",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "DryMultiplier",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "DryPenaltyLastN",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "DynatempExp",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "DynatempRange",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Fit",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "FlashAttention",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "FrequencyPenalty",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Host",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "IgnoreEos",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ImageMaxTokens",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ImageMinTokens",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Jinja",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "KeepN",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "KvOffload",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LoadMode",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Lora",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MainGpu",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MinP",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Mirostat",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MirostatEta",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MirostatTau",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Mmproj",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Parallel",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Port",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "PredictN",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "PresencePenalty",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Reasoning",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ReasoningBudget",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Repack",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "RepeatLastN",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "RepeatPenalty",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "RopeFreqBase",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "RopeFreqScale",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "RopeScale",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "RopeScalingType",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Seed",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftGpuLayers",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftModel",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftNMax",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftNMin",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftPMin",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftThreads",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftTypeK",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftTypeV",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecType",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SplitMode",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "TensorSplit",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Threads",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ThreadsBatch",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Timeout",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "TopK",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "TopNSigma",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "TopP",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "TypicalP",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "UbatchSize",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "XtcProbability",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "XtcThreshold",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "YarnAttnFactor",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "YarnBetaFast",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "YarnBetaSlow",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "YarnExtFactor",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "YarnOrigCtx",
                table: "ModelPresets");

            migrationBuilder.AlterColumn<int>(
                name: "GpuLayers",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextLength",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
