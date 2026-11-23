using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Migrations.Npgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanSuiteUnitMapColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlanImportMetaJson",
                table: "ApiTestSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuiteUnitMapJson",
                table: "ApiTestSessions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlanImportMetaJson",
                table: "ApiTestSessions");

            migrationBuilder.DropColumn(
                name: "SuiteUnitMapJson",
                table: "ApiTestSessions");
        }
    }
}
