using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace QaAutomation.Migrations.Npgsql.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Batches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    ParentWorkItemId = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    Mode = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Batches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BugSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    WorkItemId = table.Column<int>(type: "integer", nullable: false),
                    WorkItemTitle = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CurrentActivity = table.Column<string>(type: "text", nullable: true),
                    AnalysisJson = table.Column<string>(type: "text", nullable: true),
                    ActualBehavior = table.Column<string>(type: "text", nullable: true),
                    CodeContext = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    EventLogJson = table.Column<string>(type: "text", nullable: true),
                    CodeVerificationJson = table.Column<string>(type: "text", nullable: true),
                    BatchId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BugSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErrorLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Level = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: true),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Exception = table.Column<string>(type: "text", nullable: true),
                    Path = table.Column<string>(type: "text", nullable: true),
                    Context = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExistingTestCaseIndexEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanId = table.Column<int>(type: "integer", nullable: false),
                    AdoTestCaseId = table.Column<int>(type: "integer", nullable: false),
                    SuitePath = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    TitleSignature = table.Column<string>(type: "text", nullable: false),
                    StepsSummary = table.Column<string>(type: "text", nullable: true),
                    StepsHash = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExistingTestCaseIndexEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AdoOrganization = table.Column<string>(type: "text", nullable: false),
                    AdoProject = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Boards = table.Column<string>(type: "text", nullable: false),
                    TicketCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    HtmlPath = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Trigger = table.Column<string>(type: "text", nullable: false),
                    Theme = table.Column<string>(type: "text", nullable: false, defaultValue: "Executive"),
                    SummaryJson = table.Column<string>(type: "text", nullable: true),
                    EventLogJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchemaProducts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AdoProject = table.Column<string>(type: "text", nullable: false),
                    AdoRepository = table.Column<string>(type: "text", nullable: false),
                    DefaultBranch = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaProducts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "TestPlanScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlanId = table.Column<int>(type: "integer", nullable: false),
                    SuiteId = table.Column<int>(type: "integer", nullable: true),
                    ScannedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CaseCount = table.Column<int>(type: "integer", nullable: false),
                    TitlePatternSummary = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestPlanScans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TestSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    WorkItemId = table.Column<int>(type: "integer", nullable: false),
                    WorkItemTitle = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CurrentActivity = table.Column<string>(type: "text", nullable: true),
                    AnalysisJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    EventLogJson = table.Column<string>(type: "text", nullable: true),
                    AdditionalContext = table.Column<string>(type: "text", nullable: true),
                    CodeContext = table.Column<string>(type: "text", nullable: true),
                    TesterContext = table.Column<string>(type: "text", nullable: true),
                    ReferenceTestPlanId = table.Column<int>(type: "integer", nullable: true),
                    ReferenceTestSuiteId = table.Column<int>(type: "integer", nullable: true),
                    PlanNavigationHint = table.Column<string>(type: "text", nullable: true),
                    BatchId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BugDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    Preconditions = table.Column<string>(type: "text", nullable: false),
                    ReproStepsJson = table.Column<string>(type: "text", nullable: false),
                    ExpectedBehavior = table.Column<string>(type: "text", nullable: false),
                    ActualBehavior = table.Column<string>(type: "text", nullable: false),
                    SuggestedFix = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AdoBugId = table.Column<int>(type: "integer", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BugDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BugDrafts_BugSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "BugSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiTestSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    Repository = table.Column<string>(type: "text", nullable: true),
                    Branch = table.Column<string>(type: "text", nullable: true),
                    WorkItemId = table.Column<int>(type: "integer", nullable: true),
                    WorkItemTitle = table.Column<string>(type: "text", nullable: true),
                    TestPlanId = table.Column<int>(type: "integer", nullable: true),
                    TestSuiteId = table.Column<int>(type: "integer", nullable: true),
                    SourceDirsJson = table.Column<string>(type: "text", nullable: true),
                    FrontendModule = table.Column<string>(type: "text", nullable: true),
                    FrontendPath = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    InventoryJson = table.Column<string>(type: "text", nullable: true),
                    AnalysisJson = table.Column<string>(type: "text", nullable: true),
                    EventLogJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    AutoHandoff = table.Column<bool>(type: "boolean", nullable: false),
                    ActivityEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
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
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    StoredPath = table.Column<string>(type: "text", nullable: false),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Documents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineWatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    AdoPipelineId = table.Column<int>(type: "integer", nullable: false),
                    AdoPipelineName = table.Column<string>(type: "text", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    LastSeenBuildId = table.Column<int>(type: "integer", nullable: true),
                    LastPollAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastPollError = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: true),
                    LevelFilter = table.Column<string>(type: "text", nullable: true),
                    TagFilter = table.Column<string>(type: "text", nullable: true),
                    Profile = table.Column<string>(type: "text", nullable: false),
                    TestPlanId = table.Column<int>(type: "integer", nullable: true),
                    TestSuiteId = table.Column<int>(type: "integer", nullable: true),
                    TriggerSource = table.Column<string>(type: "text", nullable: false),
                    TriggerPipelineId = table.Column<int>(type: "integer", nullable: true),
                    TriggerBuildId = table.Column<int>(type: "integer", nullable: true),
                    TriggerCommitSha = table.Column<string>(type: "text", nullable: true),
                    ImpactAnalysisJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Passed = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    JunitPath = table.Column<string>(type: "text", nullable: true),
                    ReportPath = table.Column<string>(type: "text", nullable: true),
                    EventLogJson = table.Column<string>(type: "text", nullable: true),
                    AdoTestRunId = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ActivityEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    LevelBreakdownJson = table.Column<string>(type: "text", nullable: true)
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
                name: "SchemaConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    Environment = table.Column<string>(type: "text", nullable: false, defaultValue: "Default"),
                    ServiceName = table.Column<string>(type: "text", nullable: true),
                    Server = table.Column<string>(type: "text", nullable: true),
                    DbUser = table.Column<string>(type: "text", nullable: true),
                    DbDatabase = table.Column<string>(type: "text", nullable: true),
                    ConnectionString = table.Column<string>(type: "text", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaConnections_SchemaProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "SchemaProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaEntities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    RootTableName = table.Column<string>(type: "text", nullable: true),
                    IdColumn = table.Column<string>(type: "text", nullable: true),
                    ScopeColumn = table.Column<string>(type: "text", nullable: true),
                    DisplayColumnsJson = table.Column<string>(type: "text", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaEntities_SchemaProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "SchemaProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: true),
                    CommitId = table.Column<string>(type: "text", nullable: true),
                    Branch = table.Column<string>(type: "text", nullable: true),
                    ServiceCount = table.Column<int>(type: "integer", nullable: false),
                    TableCount = table.Column<int>(type: "integer", nullable: false),
                    RelationCount = table.Column<int>(type: "integer", nullable: false),
                    EntityCount = table.Column<int>(type: "integer", nullable: false),
                    StatsJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    EventLogJson = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaScans_SchemaProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "SchemaProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestCaseDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Preconditions = table.Column<string>(type: "text", nullable: false),
                    StepsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AdoTestCaseId = table.Column<int>(type: "integer", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestCaseDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestCaseDrafts_TestSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "TestSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiTestDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Preconditions = table.Column<string>(type: "text", nullable: false),
                    StepsJson = table.Column<string>(type: "text", nullable: false),
                    SchemaJson = table.Column<string>(type: "text", nullable: true),
                    SpecCode = table.Column<string>(type: "text", nullable: false),
                    SpecFilePath = table.Column<string>(type: "text", nullable: true),
                    CaseIndex = table.Column<int>(type: "integer", nullable: true),
                    Tags = table.Column<string>(type: "text", nullable: true),
                    SourceRoutesJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AdoTestCaseId = table.Column<int>(type: "integer", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<int>(type: "integer", nullable: false),
                    TestTitle = table.Column<string>(type: "text", nullable: false),
                    AdoTestCaseId = table.Column<int>(type: "integer", nullable: true),
                    Classification = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    BugDraftJson = table.Column<string>(type: "text", nullable: true),
                    BugId = table.Column<int>(type: "integer", nullable: true),
                    ArtifactPaths = table.Column<string>(type: "text", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "SchemaRelations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanId = table.Column<int>(type: "integer", nullable: false),
                    SourceTableId = table.Column<int>(type: "integer", nullable: false),
                    TargetTableId = table.Column<int>(type: "integer", nullable: false),
                    SourceColumn = table.Column<string>(type: "text", nullable: true),
                    TargetColumn = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Evidence = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaRelations_SchemaScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "SchemaScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaServices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DbContextClass = table.Column<string>(type: "text", nullable: true),
                    SourcePath = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaServices_SchemaScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "SchemaScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchemaTables",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanId = table.Column<int>(type: "integer", nullable: false),
                    ServiceId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SqlSchema = table.Column<string>(type: "text", nullable: true),
                    IsKeyless = table.Column<bool>(type: "boolean", nullable: false),
                    IsExternalRef = table.Column<bool>(type: "boolean", nullable: false),
                    OwnerServiceName = table.Column<string>(type: "text", nullable: true),
                    SourceFile = table.Column<string>(type: "text", nullable: true),
                    ColumnsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaTables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaTables_SchemaScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "SchemaScans",
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
                name: "ix_BugDrafts_SessionId",
                table: "BugDrafts",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "ix_BugSessions_BatchId",
                table: "BugSessions",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "ix_Documents_ProjectId",
                table: "Documents",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "ix_ErrorLogs_CreatedAt",
                table: "ErrorLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_ExistingTestCaseIndexEntries_ScanId",
                table: "ExistingTestCaseIndexEntries",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "ix_PipelineWatches_ProjectId_AdoPipelineId",
                table: "PipelineWatches",
                columns: new[] { "ProjectId", "AdoPipelineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_SchemaConnections_ProductId",
                table: "SchemaConnections",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "ix_SchemaEntities_ProductId_Key",
                table: "SchemaEntities",
                columns: new[] { "ProductId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_SchemaRelations_ScanId",
                table: "SchemaRelations",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "ix_SchemaRelations_SourceTableId",
                table: "SchemaRelations",
                column: "SourceTableId");

            migrationBuilder.CreateIndex(
                name: "ix_SchemaScans_ProductId_CreatedAt",
                table: "SchemaScans",
                columns: new[] { "ProductId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_SchemaServices_ScanId",
                table: "SchemaServices",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "ix_SchemaTables_ScanId",
                table: "SchemaTables",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "ix_SchemaTables_ServiceId",
                table: "SchemaTables",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "ix_TestCaseDrafts_SessionId",
                table: "TestCaseDrafts",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "ix_TestFailures_RunId",
                table: "TestFailures",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "ix_TestPlanScans_PlanId_SuiteId",
                table: "TestPlanScans",
                columns: new[] { "PlanId", "SuiteId" });

            migrationBuilder.CreateIndex(
                name: "ix_TestRuns_ProjectId_CreatedAt",
                table: "TestRuns",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_TestSessions_BatchId",
                table: "TestSessions",
                column: "BatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiTestDrafts");

            migrationBuilder.DropTable(
                name: "Batches");

            migrationBuilder.DropTable(
                name: "BugDrafts");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.DropTable(
                name: "ErrorLogs");

            migrationBuilder.DropTable(
                name: "ExistingTestCaseIndexEntries");

            migrationBuilder.DropTable(
                name: "PipelineWatches");

            migrationBuilder.DropTable(
                name: "Reports");

            migrationBuilder.DropTable(
                name: "SchemaConnections");

            migrationBuilder.DropTable(
                name: "SchemaEntities");

            migrationBuilder.DropTable(
                name: "SchemaRelations");

            migrationBuilder.DropTable(
                name: "SchemaServices");

            migrationBuilder.DropTable(
                name: "SchemaTables");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "TestCaseDrafts");

            migrationBuilder.DropTable(
                name: "TestFailures");

            migrationBuilder.DropTable(
                name: "TestPlanScans");

            migrationBuilder.DropTable(
                name: "ApiTestSessions");

            migrationBuilder.DropTable(
                name: "BugSessions");

            migrationBuilder.DropTable(
                name: "SchemaScans");

            migrationBuilder.DropTable(
                name: "TestSessions");

            migrationBuilder.DropTable(
                name: "TestRuns");

            migrationBuilder.DropTable(
                name: "SchemaProducts");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
