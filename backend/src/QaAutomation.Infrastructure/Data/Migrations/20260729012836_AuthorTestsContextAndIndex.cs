using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuthorTestsContextAndIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdditionalContext",
                table: "TestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CodeContext",
                table: "TestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanNavigationHint",
                table: "TestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReferenceTestPlanId",
                table: "TestSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReferenceTestSuiteId",
                table: "TestSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TesterContext",
                table: "TestSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExistingTestCaseIndexEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    AdoTestCaseId = table.Column<int>(type: "INTEGER", nullable: false),
                    SuitePath = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    TitleSignature = table.Column<string>(type: "TEXT", nullable: false),
                    StepsSummary = table.Column<string>(type: "TEXT", nullable: true),
                    StepsHash = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExistingTestCaseIndexEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TestPlanScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    SuiteId = table.Column<int>(type: "INTEGER", nullable: true),
                    ScannedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CaseCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TitlePatternSummary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestPlanScans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ExistingTestCaseIndexEntries_ScanId",
                table: "ExistingTestCaseIndexEntries",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "ix_TestPlanScans_PlanId_SuiteId",
                table: "TestPlanScans",
                columns: new[] { "PlanId", "SuiteId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExistingTestCaseIndexEntries");

            migrationBuilder.DropTable(
                name: "TestPlanScans");

            migrationBuilder.DropColumn(
                name: "AdditionalContext",
                table: "TestSessions");

            migrationBuilder.DropColumn(
                name: "CodeContext",
                table: "TestSessions");

            migrationBuilder.DropColumn(
                name: "PlanNavigationHint",
                table: "TestSessions");

            migrationBuilder.DropColumn(
                name: "ReferenceTestPlanId",
                table: "TestSessions");

            migrationBuilder.DropColumn(
                name: "ReferenceTestSuiteId",
                table: "TestSessions");

            migrationBuilder.DropColumn(
                name: "TesterContext",
                table: "TestSessions");
        }
    }
}
