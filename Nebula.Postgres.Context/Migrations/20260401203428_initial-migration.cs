using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class initialmigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    Classification = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "commands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandId = table.Column<long>(type: "bigint", nullable: true),
                    Objective = table.Column<string>(type: "text", nullable: false),
                    Command = table.Column<string>(type: "text", nullable: false),
                    OsType = table.Column<string>(type: "text", nullable: false),
                    Executed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ExecutionResult = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_commands_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "command_verifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsCorrect = table.Column<bool>(type: "boolean", nullable: false),
                    IsSafe = table.Column<bool>(type: "boolean", nullable: false),
                    VerificationNotes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_command_verifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_command_verifications_commands_CommandId",
                        column: x => x.CommandId,
                        principalTable: "commands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_command_verifications_CommandId",
                table: "command_verifications",
                column: "CommandId");

            migrationBuilder.CreateIndex(
                name: "IX_command_verifications_CreatedAt",
                table: "command_verifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_commands_CreatedAt",
                table: "commands",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_commands_Executed",
                table: "commands",
                column: "Executed");

            migrationBuilder.CreateIndex(
                name: "IX_commands_OsType",
                table: "commands",
                column: "OsType");

            migrationBuilder.CreateIndex(
                name: "IX_commands_RequestId",
                table: "commands",
                column: "RequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "command_verifications");

            migrationBuilder.DropTable(
                name: "commands");

            migrationBuilder.DropTable(
                name: "requests");
        }
    }
}
