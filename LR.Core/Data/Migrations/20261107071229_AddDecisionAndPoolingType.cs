using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LR.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionAndPoolingType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GgufDecisionType",
                table: "ModelPresets",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionType",
                table: "LocalModels",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PoolingType",
                table: "LocalModels",
                type: "INTEGER",
                nullable: true);

            // Backfill from the GGUF key/values already stored per model, so existing libraries
            // classify decision/embedding models without a manual Refresh. The keys are
            // "{arch}.decision.type" / "{arch}.pooling_type"; quoting the key keeps its dots literal.
            migrationBuilder.Sql("""
                UPDATE LocalModels
                SET DecisionType = json_extract(AllKvPairsJson, '$."' || Architecture || '.decision.type"'),
                    PoolingType  = json_extract(AllKvPairsJson, '$."' || Architecture || '.pooling_type"')
                WHERE AllKvPairsJson IS NOT NULL AND json_valid(AllKvPairsJson) AND Architecture IS NOT NULL;
                """);
            migrationBuilder.Sql("""
                UPDATE ModelPresets
                SET GgufDecisionType = (SELECT m.DecisionType FROM LocalModels m WHERE m.Id = ModelPresets.ModelId)
                WHERE ModelId IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GgufDecisionType",
                table: "ModelPresets");

            migrationBuilder.DropColumn(
                name: "DecisionType",
                table: "LocalModels");

            migrationBuilder.DropColumn(
                name: "PoolingType",
                table: "LocalModels");
        }
    }
}
