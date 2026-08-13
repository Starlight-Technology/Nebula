using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class agent_phase1_run_details : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ApprovedByUser",
                table: "commands",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoApproved",
                table: "commands",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExecutedAt",
                table: "commands",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExitCode",
                table: "commands",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Required",
                table: "commands",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SafetyDecision",
                table: "commands",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shell",
                table: "commands",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Skipped",
                table: "commands",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "StandardError",
                table: "commands",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StandardOutput",
                table: "commands",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkingDirectory",
                table: "commands",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ApprovedByUser",
                table: "agent_step_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoApproved",
                table: "agent_step_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SafetyDecision",
                table: "agent_step_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Shell",
                table: "agent_step_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentPlan",
                table: "agent_runs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepId = table.Column<Guid>(type: "uuid", nullable: false),
                    Objective = table.Column<string>(type: "text", nullable: false),
                    Command = table.Column<string>(type: "text", nullable: true),
                    Decision = table.Column<string>(type: "text", nullable: true),
                    ApprovedByUser = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AutoApproved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_approvals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_approvals_agent_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "agent_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: true),
                    ContentHash = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_artifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_artifacts_agent_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "agent_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_approvals_RunId",
                table: "agent_approvals",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_artifacts_RunId",
                table: "agent_artifacts",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_approvals");

            migrationBuilder.DropTable(
                name: "agent_artifacts");

            migrationBuilder.DropColumn(
                name: "ApprovedByUser",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "AutoApproved",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "ExecutedAt",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "ExitCode",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "Required",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "SafetyDecision",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "Shell",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "Skipped",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "StandardError",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "StandardOutput",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "WorkingDirectory",
                table: "commands");

            migrationBuilder.DropColumn(
                name: "ApprovedByUser",
                table: "agent_step_records");

            migrationBuilder.DropColumn(
                name: "AutoApproved",
                table: "agent_step_records");

            migrationBuilder.DropColumn(
                name: "SafetyDecision",
                table: "agent_step_records");

            migrationBuilder.DropColumn(
                name: "Shell",
                table: "agent_step_records");

            migrationBuilder.DropColumn(
                name: "CurrentPlan",
                table: "agent_runs");
        }
    }
}
