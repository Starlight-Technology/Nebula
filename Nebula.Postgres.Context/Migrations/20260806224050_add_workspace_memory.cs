using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class add_workspace_memory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspace_memory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Workspace = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Value = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Evidence = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_memory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_memory_Workspace_CreatedAt",
                table: "workspace_memory",
                columns: new[] { "Workspace", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workspace_memory_Workspace_Kind_Key",
                table: "workspace_memory",
                columns: new[] { "Workspace", "Kind", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_memory");
        }
    }
}
