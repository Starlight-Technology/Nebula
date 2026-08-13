using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class add_agent_run_workspace_root : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkspaceRoot",
                table: "agent_runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkspaceRoot",
                table: "agent_runs");
        }
    }
}
