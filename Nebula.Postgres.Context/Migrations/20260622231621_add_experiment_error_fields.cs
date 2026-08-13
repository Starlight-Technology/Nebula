using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class add_experiment_error_fields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvironmentFingerprint",
                table: "knowledge_experiments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCategory",
                table: "knowledge_experiments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "knowledge_experiments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalExperimentId",
                table: "knowledge_experiments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedCommand",
                table: "knowledge_experiments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "knowledge_experiments",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvironmentFingerprint",
                table: "knowledge_experiments");

            migrationBuilder.DropColumn(
                name: "ErrorCategory",
                table: "knowledge_experiments");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "knowledge_experiments");

            migrationBuilder.DropColumn(
                name: "OriginalExperimentId",
                table: "knowledge_experiments");

            migrationBuilder.DropColumn(
                name: "ResolvedCommand",
                table: "knowledge_experiments");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "knowledge_experiments");
        }
    }
}
