using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class add_learning_engine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConversationContextId",
                table: "conversation_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "knowledge_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Topic = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    NormalizedCommand = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "text", nullable: true),
                    OS = table.Column<string>(type: "text", nullable: true),
                    Shell = table.Column<string>(type: "text", nullable: true),
                    SourceUrl = table.Column<string>(type: "text", nullable: false),
                    SourceScore = table.Column<double>(type: "double precision", nullable: false),
                    ClassificationConfidence = table.Column<double>(type: "double precision", nullable: false),
                    SafetyScore = table.Column<double>(type: "double precision", nullable: false),
                    VerificationScore = table.Column<double>(type: "double precision", nullable: false),
                    FinalScore = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_experiments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    VerificationKind = table.Column<string>(type: "text", nullable: false),
                    CommandExecuted = table.Column<string>(type: "text", nullable: true),
                    TestCode = table.Column<string>(type: "text", nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    StdOut = table.Column<string>(type: "text", nullable: true),
                    StdErr = table.Column<string>(type: "text", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    EvidenceHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_experiments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_experiments_knowledge_items_KnowledgeItemId",
                        column: x => x.KnowledgeItemId,
                        principalTable: "knowledge_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Publisher = table.Column<string>(type: "text", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    TrustScore = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_sources_knowledge_items_KnowledgeItemId",
                        column: x => x.KnowledgeItemId,
                        principalTable: "knowledge_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_experiments_KnowledgeItemId",
                table: "knowledge_experiments",
                column: "KnowledgeItemId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_Domain",
                table: "knowledge_items",
                column: "Domain");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_FinalScore",
                table: "knowledge_items",
                column: "FinalScore");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_items_Topic",
                table: "knowledge_items",
                column: "Topic");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_sources_KnowledgeItemId",
                table: "knowledge_sources",
                column: "KnowledgeItemId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_sources_Url",
                table: "knowledge_sources",
                column: "Url");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_experiments");

            migrationBuilder.DropTable(
                name: "knowledge_sources");

            migrationBuilder.DropTable(
                name: "knowledge_items");

            migrationBuilder.DropColumn(
                name: "ConversationContextId",
                table: "conversation_messages");
        }
    }
}
