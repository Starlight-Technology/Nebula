using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nebula.Postgres.Context.Migrations
{
    /// <inheritdoc />
    public partial class add_sandbox_settings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sandbox_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Workspace = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Image = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MemoryLimitMb = table.Column<long>(type: "bigint", nullable: false),
                    CpuLimit = table.Column<double>(type: "double precision", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandbox_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sandbox_settings_UserId_Workspace",
                table: "sandbox_settings",
                columns: new[] { "UserId", "Workspace" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sandbox_settings");
        }
    }
}
