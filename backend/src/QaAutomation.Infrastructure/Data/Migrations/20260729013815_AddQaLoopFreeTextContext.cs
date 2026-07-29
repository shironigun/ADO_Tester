using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQaLoopFreeTextContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdditionalContext",
                table: "ApiTestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerateContext",
                table: "ApiTestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastFeedback",
                table: "ApiTestSessions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdditionalContext",
                table: "ApiTestSessions");

            migrationBuilder.DropColumn(
                name: "GenerateContext",
                table: "ApiTestSessions");

            migrationBuilder.DropColumn(
                name: "LastFeedback",
                table: "ApiTestSessions");
        }
    }
}
