using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExtendedLlamaCppSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CacheReuse",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContextShift",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlVector",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ControlVectorLayerEnd",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ControlVectorLayerStart",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlVectorScaled",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CpuMoe",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DrySequenceBreaker",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FitCtx",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FitTarget",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KvUnified",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoraScaled",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MmprojAuto",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MmprojDevice",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MmprojOffload",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MmprojUrl",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MtmdBatchMaxTokens",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NCpuMoe",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OverrideTensor",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasoningBudgetMessage",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SamplerSeq",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Samplers",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SleepIdleSeconds",
                table: "ModelPresets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "SlotPromptSimilarity",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "SpecDraftPSplit",
                table: "ModelPresets",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheReuse",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ContextShift",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ControlVector",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ControlVectorLayerEnd",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ControlVectorLayerStart",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ControlVectorScaled",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "CpuMoe",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "DrySequenceBreaker",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "FitCtx",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "FitTarget",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "KvUnified",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "LoraScaled",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MmprojAuto",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MmprojDevice",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MmprojOffload",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MmprojUrl",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "MtmdBatchMaxTokens",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "NCpuMoe",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "OverrideTensor",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ReasoningBudgetMessage",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SamplerSeq",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "Samplers",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SleepIdleSeconds",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SlotPromptSimilarity",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "SpecDraftPSplit",
                table: "ModelPresets");
        }
    }
}
