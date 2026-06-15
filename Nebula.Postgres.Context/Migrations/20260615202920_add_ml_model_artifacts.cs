using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class add_ml_model_artifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ml_model_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ModelData = table.Column<byte[]>(type: "bytea", nullable: false),
                    SchemaJson = table.Column<string>(type: "jsonb", nullable: true),
                    Accuracy = table.Column<double>(type: "double precision", nullable: true),
                    F1Score = table.Column<double>(type: "double precision", nullable: true),
                    TrainingDatasetHash = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ml_model_artifacts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ml_model_artifacts_Name",
                table: "ml_model_artifacts",
                column: "Name",
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_ml_model_artifacts_Name_Version",
                table: "ml_model_artifacts",
                columns: new[] { "Name", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ml_model_artifacts");
        }
    }
}
