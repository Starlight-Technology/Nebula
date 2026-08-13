using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class add_free_learning_research : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtractedContent",
                table: "knowledge_sources",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Examples",
                table: "knowledge_items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "knowledge_items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Warnings",
                table: "knowledge_items",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "fetched_page_cache",
                columns: table => new
                {
                    Url = table.Column<string>(type: "text", nullable: false),
                    Html = table.Column<string>(type: "text", nullable: false),
                    HtmlHash = table.Column<string>(type: "text", nullable: false),
                    RetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fetched_page_cache", x => x.Url);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_facts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fact = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_facts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_facts_knowledge_items_KnowledgeItemId",
                        column: x => x.KnowledgeItemId,
                        principalTable: "knowledge_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fetched_page_cache_ExpiresAt",
                table: "fetched_page_cache",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_facts_KnowledgeItemId",
                table: "knowledge_facts",
                column: "KnowledgeItemId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_facts_SourceUrl",
                table: "knowledge_facts",
                column: "SourceUrl");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fetched_page_cache");

            migrationBuilder.DropTable(
                name: "knowledge_facts");

            migrationBuilder.DropColumn(
                name: "ExtractedContent",
                table: "knowledge_sources");

            migrationBuilder.DropColumn(
                name: "Examples",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "Warnings",
                table: "knowledge_items");
        }
    }
}
