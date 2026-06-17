using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class add_offline_learning_metadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ConfidenceScore",
                table: "knowledge_items",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "Hash",
                table: "knowledge_items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsDangerousInstruction",
                table: "knowledge_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsExecutableAdvice",
                table: "knowledge_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsValidated",
                table: "knowledge_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "knowledge_items",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<int>(
                name: "ObservationCount",
                table: "knowledge_items",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "RiskLevel",
                table: "knowledge_items",
                type: "text",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "SourceName",
                table: "knowledge_items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "knowledge_items",
                type: "text",
                nullable: false,
                defaultValue: "WebResearch");

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "knowledge_items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ValidationNotes",
                table: "knowledge_items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderName",
                table: "knowledge_sources",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "knowledge_sources",
                type: "text",
                nullable: false,
                defaultValue: "WebResearch");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_Hash",
                table: "knowledge_items",
                column: "Hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_knowledge_items_Hash",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "ConfidenceScore",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "Hash",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "IsDangerousInstruction",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "IsExecutableAdvice",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "IsValidated",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "ObservationCount",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "RiskLevel",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "SourceName",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "ValidationNotes",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "ProviderName",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "knowledge_sources");
        }
    }
}
