using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Migrations.Npgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddQaLoopUiPhase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasUiPhase",
                table: "ApiTestSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UiStatus",
                table: "ApiTestSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Bucket",
                table: "ApiTestDrafts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftKind",
                table: "ApiTestDrafts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasUiPhase",
                table: "ApiTestSessions");

            migrationBuilder.DropColumn(
                name: "UiStatus",
                table: "ApiTestSessions");

            migrationBuilder.DropColumn(
                name: "Bucket",
                table: "ApiTestDrafts");

            migrationBuilder.DropColumn(
                name: "DraftKind",
                table: "ApiTestDrafts");
        }
    }
}
