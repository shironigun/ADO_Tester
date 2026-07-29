using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace QaAutomation.Migrations.Npgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddScriptBoardRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScriptBoardRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    SelectionSummary = table.Column<string>(type: "text", nullable: false),
                    SpecFilePathsJson = table.Column<string>(type: "text", nullable: false),
                    Headed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Workers = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Passed = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    JunitPath = table.Column<string>(type: "text", nullable: true),
                    ReportPath = table.Column<string>(type: "text", nullable: true),
                    EventLogJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptBoardRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ScriptBoardRuns_ProjectId_CreatedAt",
                table: "ScriptBoardRuns",
                columns: new[] { "ProjectId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScriptBoardRuns");
        }
    }
}
