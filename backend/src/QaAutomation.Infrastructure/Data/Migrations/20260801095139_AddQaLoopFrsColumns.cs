using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQaLoopFrsColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodeScanJson",
                table: "ApiTestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FrsSlug",
                table: "ApiTestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KbContextJson",
                table: "ApiTestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUiRunId",
                table: "ApiTestSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UiAnalysisJson",
                table: "ApiTestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UiTestSuiteId",
                table: "ApiTestSessions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodeScanJson",
                table: "ApiTestSessions");

            migrationBuilder.DropColumn(
                name: "FrsSlug",
                table: "ApiTestSessions");

            migrationBuilder.DropColumn(
                name: "KbContextJson",
                table: "ApiTestSessions");

            migrationBuilder.DropColumn(
                name: "LastUiRunId",
                table: "ApiTestSessions");

            migrationBuilder.DropColumn(
                name: "UiAnalysisJson",
                table: "ApiTestSessions");

            migrationBuilder.DropColumn(
                name: "UiTestSuiteId",
                table: "ApiTestSessions");
        }
    }
}
