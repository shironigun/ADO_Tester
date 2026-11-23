using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Migrations.Npgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddKbFolderPathAndFrsColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FolderPath",
                table: "Documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CodeScanJson",
                table: "ApiTestSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FrsSlug",
                table: "ApiTestSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KbContextJson",
                table: "ApiTestSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastUiRunId",
                table: "ApiTestSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UiAnalysisJson",
                table: "ApiTestSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UiTestSuiteId",
                table: "ApiTestSessions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FolderPath",
                table: "Documents");

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
