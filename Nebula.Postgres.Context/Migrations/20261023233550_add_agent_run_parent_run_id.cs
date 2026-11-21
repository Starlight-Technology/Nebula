using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class add_agent_run_parent_run_id : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentRunId",
                table: "agent_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_ParentRunId",
                table: "agent_runs",
                column: "ParentRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agent_runs_ParentRunId",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "ParentRunId",
                table: "agent_runs");
        }
    }
}
