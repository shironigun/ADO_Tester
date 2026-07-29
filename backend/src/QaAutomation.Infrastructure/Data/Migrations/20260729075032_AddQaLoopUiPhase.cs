using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Infrastructure.Data.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UiStatus",
                table: "ApiTestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Bucket",
                table: "ApiTestDrafts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftKind",
                table: "ApiTestDrafts",
                type: "TEXT",
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
