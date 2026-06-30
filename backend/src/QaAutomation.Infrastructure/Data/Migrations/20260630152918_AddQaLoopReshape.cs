using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QaAutomation.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQaLoopReshape : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiTestSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Module = table.Column<string>(type: "TEXT", nullable: false),
                    Repository = table.Column<string>(type: "TEXT", nullable: true),
                    Branch = table.Column<string>(type: "TEXT", nullable: true),
                    WorkItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    WorkItemTitle = table.Column<string>(type: "TEXT", nullable: true),
                    TestPlanId = table.Column<int>(type: "INTEGER", nullable: true),
                    TestSuiteId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    InventoryJson = table.Column<string>(type: "TEXT", nullable: true),
                    AnalysisJson = table.Column<string>(type: "TEXT", nullable: true),
                    EventLogJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    AutoHandoff = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActivityEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTestSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiTestSessions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineWatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    AdoPipelineId = table.Column<int>(type: "INTEGER", nullable: false),
                    AdoPipelineName = table.Column<string>(type: "TEXT", nullable: false),
                    Module = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenBuildId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastPollAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPollError = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineWatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PipelineWatches_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    Module = table.Column<string>(type: "TEXT", nullable: true),
                    LevelFilter = table.Column<string>(type: "TEXT", nullable: true),
                    TagFilter = table.Column<string>(type: "TEXT", nullable: true),
                    Profile = table.Column<string>(type: "TEXT", nullable: false),
                    TestPlanId = table.Column<int>(type: "INTEGER", nullable: true),
                    TestSuiteId = table.Column<int>(type: "INTEGER", nullable: true),
                    TriggerSource = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerPipelineId = table.Column<int>(type: "INTEGER", nullable: true),
                    TriggerBuildId = table.Column<int>(type: "INTEGER", nullable: true),
                    TriggerCommitSha = table.Column<string>(type: "TEXT", nullable: true),
                    ImpactAnalysisJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Passed = table.Column<int>(type: "INTEGER", nullable: false),
                    Failed = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    JunitPath = table.Column<string>(type: "TEXT", nullable: true),
                    ReportPath = table.Column<string>(type: "TEXT", nullable: true),
                    AdoTestRunId = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActivityEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestRuns_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiTestDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Preconditions = table.Column<string>(type: "TEXT", nullable: false),
                    StepsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaJson = table.Column<string>(type: "TEXT", nullable: true),
                    SpecCode = table.Column<string>(type: "TEXT", nullable: false),
                    SpecFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    SourceRoutesJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AdoTestCaseId = table.Column<int>(type: "INTEGER", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTestDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiTestDrafts_ApiTestSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ApiTestSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestFailures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<int>(type: "INTEGER", nullable: false),
                    TestTitle = table.Column<string>(type: "TEXT", nullable: false),
                    AdoTestCaseId = table.Column<int>(type: "INTEGER", nullable: true),
                    Classification = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    BugDraftJson = table.Column<string>(type: "TEXT", nullable: true),
                    BugId = table.Column<int>(type: "INTEGER", nullable: true),
                    ArtifactPaths = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestFailures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestFailures_TestRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "TestRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ApiTestDrafts_SessionId",
                table: "ApiTestDrafts",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "ix_ApiTestSessions_ProjectId_CreatedAt",
                table: "ApiTestSessions",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_PipelineWatches_ProjectId_AdoPipelineId",
                table: "PipelineWatches",
                columns: new[] { "ProjectId", "AdoPipelineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_TestFailures_RunId",
                table: "TestFailures",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "ix_TestRuns_ProjectId_CreatedAt",
                table: "TestRuns",
                columns: new[] { "ProjectId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiTestDrafts");

            migrationBuilder.DropTable(
                name: "PipelineWatches");

            migrationBuilder.DropTable(
                name: "TestFailures");

            migrationBuilder.DropTable(
                name: "ApiTestSessions");

            migrationBuilder.DropTable(
                name: "TestRuns");
        }
    }
}
