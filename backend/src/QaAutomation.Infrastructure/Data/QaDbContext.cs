using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using QaAutomation.Core.Models;

namespace QaAutomation.Infrastructure.Data;

/// <summary>
/// EF Core 8 + SQLite context over the frozen schema (spec §7.4 / checklist B1).
/// Table and column names are PascalCase to match the original Alembic-built <c>qa.db</c> verbatim,
/// so the D6 adoption path reads the live database with no schema drift. Enums persist as their exact
/// string name via <c>HasConversion&lt;string&gt;()</c> — except <see cref="BatchModule"/>/<see cref="BatchMode"/>,
/// which use kebab/lowercase wire+DB strings through a custom converter.
/// </summary>
public sealed class QaDbContext : DbContext
{
    public QaDbContext(DbContextOptions<QaDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<KnowledgebaseDocument> Documents => Set<KnowledgebaseDocument>();
    public DbSet<TestSession> TestSessions => Set<TestSession>();
    public DbSet<TestCaseDraft> TestCaseDrafts => Set<TestCaseDraft>();
    public DbSet<BugSession> BugSessions => Set<BugSession>();
    public DbSet<BugDraft> BugDrafts => Set<BugDraft>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();

    // ── Existing-test dedup DbSets ────────────────────────────────────────────
    public DbSet<TestPlanScan> TestPlanScans => Set<TestPlanScan>();
    public DbSet<ExistingTestCaseIndexEntry> ExistingTestCaseIndexEntries => Set<ExistingTestCaseIndexEntry>();

    // ── QA Loop DbSets ────────────────────────────────────────────────────────
    public DbSet<ApiTestSession> ApiTestSessions => Set<ApiTestSession>();
    public DbSet<ApiTestDraft> ApiTestDrafts => Set<ApiTestDraft>();
    public DbSet<TestRun> TestRuns => Set<TestRun>();
    public DbSet<TestFailure> TestFailures => Set<TestFailure>();
    public DbSet<PipelineWatch> PipelineWatches => Set<PipelineWatch>();

    // ── DB Schema Explorer DbSets ─────────────────────────────────────────────
    public DbSet<SchemaProduct> SchemaProducts => Set<SchemaProduct>();
    public DbSet<SchemaScan> SchemaScans => Set<SchemaScan>();
    public DbSet<SchemaService> SchemaServices => Set<SchemaService>();
    public DbSet<SchemaTable> SchemaTables => Set<SchemaTable>();
    public DbSet<SchemaRelation> SchemaRelations => Set<SchemaRelation>();
    public DbSet<SchemaEntity> SchemaEntities => Set<SchemaEntity>();
    public DbSet<SchemaConnection> SchemaConnections => Set<SchemaConnection>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // BatchModule/BatchMode store their kebab/lowercase wire value (NOT the C# member name).
        var moduleConverter = new ValueConverter<BatchModule, string>(
            v => v.ToWire(), v => BatchEnumExtensions.ToBatchModule(v));
        var modeConverter = new ValueConverter<BatchMode?, string?>(
            v => v == null ? null : v.Value.ToWire(),
            v => v == null ? null : BatchEnumExtensions.ToBatchMode(v));

        // QA Loop wire-string enum converters (kebab/snake_case DB values).
        var testLevelConverter = new ValueConverter<TestLevel, string>(
            v => v.ToWire(), v => QaLoopEnumExtensions.ToTestLevel(v));
        var failureClassConverter = new ValueConverter<FailureClassification, string>(
            v => v.ToWire(), v => QaLoopEnumExtensions.ToFailureClassification(v));
        var triggerSourceConverter = new ValueConverter<TriggerSource, string>(
            v => v.ToWire(), v => QaLoopEnumExtensions.ToTriggerSource(v));
        var dmsProfileConverter = new ValueConverter<DmsProfile, string>(
            v => v.ToWire(), v => QaLoopEnumExtensions.ToDmsProfile(v));

        model.Entity<Project>(e =>
        {
            e.ToTable("Projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasColumnName("Name").IsRequired();
            e.Property(x => x.AdoOrganization).HasColumnName("AdoOrganization").IsRequired();
            e.Property(x => x.AdoProject).HasColumnName("AdoProject").IsRequired();
        });

        model.Entity<AppSetting>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("Key");
            e.Property(x => x.Value).HasColumnName("Value").IsRequired();
        });

        model.Entity<KnowledgebaseDocument>(e =>
        {
            e.ToTable("Documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProjectId).HasColumnName("ProjectId").IsRequired();
            e.Property(x => x.FileName).HasColumnName("FileName").IsRequired();
            e.Property(x => x.StoredPath).HasColumnName("StoredPath").IsRequired();
            e.Property(x => x.ChunkCount).HasColumnName("ChunkCount").IsRequired();
            e.Property(x => x.SizeBytes).HasColumnName("SizeBytes").IsRequired();
            e.Property(x => x.UploadedAt).HasColumnName("UploadedAt").IsRequired();
            e.HasIndex(x => x.ProjectId).HasDatabaseName("ix_Documents_ProjectId");
            // Documents.ProjectId → Projects.Id (CASCADE) is the one real FK on a *Id column.
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<TestSession>(e =>
        {
            e.ToTable("TestSessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProjectId).HasColumnName("ProjectId").IsRequired();
            e.Property(x => x.WorkItemId).HasColumnName("WorkItemId").IsRequired();
            e.Property(x => x.WorkItemTitle).HasColumnName("WorkItemTitle").IsRequired();
            e.Property(x => x.Status).HasColumnName("Status").HasConversion<string>().IsRequired();
            e.Property(x => x.CurrentActivity).HasColumnName("CurrentActivity");
            e.Property(x => x.AnalysisJson).HasColumnName("AnalysisJson");
            e.Property(x => x.ErrorMessage).HasColumnName("ErrorMessage");
            e.Property(x => x.EventLogJson).HasColumnName("EventLogJson");
            // Author Tests enhancements (AuthorTestsContextAndIndex migration).
            e.Property(x => x.AdditionalContext).HasColumnName("AdditionalContext");
            e.Property(x => x.CodeContext).HasColumnName("CodeContext");
            e.Property(x => x.TesterContext).HasColumnName("TesterContext");
            e.Property(x => x.ReferenceTestPlanId).HasColumnName("ReferenceTestPlanId");
            e.Property(x => x.ReferenceTestSuiteId).HasColumnName("ReferenceTestSuiteId");
            e.Property(x => x.PlanNavigationHint).HasColumnName("PlanNavigationHint");
            e.Property(x => x.BatchId).HasColumnName("BatchId");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").IsRequired();
            // BatchId is a plain indexed int — NO FK constraint in the live DB (spec §5.3).
            e.HasIndex(x => x.BatchId).HasDatabaseName("ix_TestSessions_BatchId");
            e.HasMany(x => x.Drafts).WithOne().HasForeignKey(d => d.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<TestPlanScan>(e =>
        {
            e.ToTable("TestPlanScans");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.PlanId).HasColumnName("PlanId").IsRequired();
            e.Property(x => x.SuiteId).HasColumnName("SuiteId");
            e.Property(x => x.ScannedAt).HasColumnName("ScannedAt").IsRequired();
            e.Property(x => x.CaseCount).HasColumnName("CaseCount").IsRequired();
            e.Property(x => x.TitlePatternSummary).HasColumnName("TitlePatternSummary");
            e.HasIndex(x => new { x.PlanId, x.SuiteId }).HasDatabaseName("ix_TestPlanScans_PlanId_SuiteId");
        });

        model.Entity<ExistingTestCaseIndexEntry>(e =>
        {
            e.ToTable("ExistingTestCaseIndexEntries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ScanId).HasColumnName("ScanId").IsRequired();
            e.Property(x => x.AdoTestCaseId).HasColumnName("AdoTestCaseId").IsRequired();
            e.Property(x => x.SuitePath).HasColumnName("SuitePath").IsRequired();
            e.Property(x => x.Title).HasColumnName("Title").IsRequired();
            e.Property(x => x.TitleSignature).HasColumnName("TitleSignature").IsRequired();
            e.Property(x => x.StepsSummary).HasColumnName("StepsSummary");
            e.Property(x => x.StepsHash).HasColumnName("StepsHash");
            e.HasIndex(x => x.ScanId).HasDatabaseName("ix_ExistingTestCaseIndexEntries_ScanId");
        });

        model.Entity<TestCaseDraft>(e =>
        {
            e.ToTable("TestCaseDrafts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.SessionId).HasColumnName("SessionId").IsRequired();
            e.Property(x => x.Title).HasColumnName("Title").IsRequired();
            e.Property(x => x.Preconditions).HasColumnName("Preconditions").IsRequired();
            e.Property(x => x.StepsJson).HasColumnName("StepsJson").IsRequired();
            e.Property(x => x.Status).HasColumnName("Status").HasConversion<string>().IsRequired();
            e.Property(x => x.AdoTestCaseId).HasColumnName("AdoTestCaseId");
            e.Property(x => x.SortOrder).HasColumnName("SortOrder").IsRequired();
            e.HasIndex(x => x.SessionId).HasDatabaseName("ix_TestCaseDrafts_SessionId");
        });

        model.Entity<BugSession>(e =>
        {
            e.ToTable("BugSessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProjectId).HasColumnName("ProjectId").IsRequired();
            e.Property(x => x.WorkItemId).HasColumnName("WorkItemId").IsRequired();
            e.Property(x => x.WorkItemTitle).HasColumnName("WorkItemTitle").IsRequired();
            e.Property(x => x.Status).HasColumnName("Status").HasConversion<string>().IsRequired();
            e.Property(x => x.CurrentActivity).HasColumnName("CurrentActivity");
            e.Property(x => x.AnalysisJson).HasColumnName("AnalysisJson");
            e.Property(x => x.ActualBehavior).HasColumnName("ActualBehavior");
            e.Property(x => x.CodeContext).HasColumnName("CodeContext");
            e.Property(x => x.ErrorMessage).HasColumnName("ErrorMessage");
            e.Property(x => x.EventLogJson).HasColumnName("EventLogJson");
            e.Property(x => x.CodeVerificationJson).HasColumnName("CodeVerificationJson");
            e.Property(x => x.BatchId).HasColumnName("BatchId");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").IsRequired();
            e.HasIndex(x => x.BatchId).HasDatabaseName("ix_BugSessions_BatchId");
            e.HasMany(x => x.Drafts).WithOne().HasForeignKey(d => d.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<BugDraft>(e =>
        {
            e.ToTable("BugDrafts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.SessionId).HasColumnName("SessionId").IsRequired();
            e.Property(x => x.Title).HasColumnName("Title").IsRequired();
            e.Property(x => x.Severity).HasColumnName("Severity").HasConversion<string>().IsRequired();
            e.Property(x => x.Preconditions).HasColumnName("Preconditions").IsRequired();
            e.Property(x => x.ReproStepsJson).HasColumnName("ReproStepsJson").IsRequired();
            e.Property(x => x.ExpectedBehavior).HasColumnName("ExpectedBehavior").IsRequired();
            e.Property(x => x.ActualBehavior).HasColumnName("ActualBehavior").IsRequired();
            e.Property(x => x.SuggestedFix).HasColumnName("SuggestedFix").IsRequired();
            e.Property(x => x.Status).HasColumnName("Status").HasConversion<string>().IsRequired();
            e.Property(x => x.AdoBugId).HasColumnName("AdoBugId");
            e.Property(x => x.SortOrder).HasColumnName("SortOrder").IsRequired();
            e.HasIndex(x => x.SessionId).HasDatabaseName("ix_BugDrafts_SessionId");
        });

        model.Entity<Report>(e =>
        {
            e.ToTable("Reports");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProjectId).HasColumnName("ProjectId").IsRequired();
            e.Property(x => x.FromUtc).HasColumnName("FromUtc").IsRequired();
            e.Property(x => x.ToUtc).HasColumnName("ToUtc").IsRequired();
            e.Property(x => x.Boards).HasColumnName("Boards").IsRequired();
            e.Property(x => x.TicketCount).HasColumnName("TicketCount").IsRequired();
            e.Property(x => x.Status).HasColumnName("Status").HasConversion<string>().IsRequired();
            e.Property(x => x.HtmlPath).HasColumnName("HtmlPath");
            e.Property(x => x.ErrorMessage).HasColumnName("ErrorMessage");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.Title).HasColumnName("Title").IsRequired();
            e.Property(x => x.Trigger).HasColumnName("Trigger").IsRequired();
            e.Property(x => x.Theme).HasColumnName("Theme").HasConversion<string>().IsRequired().HasDefaultValue(ReportTheme.Executive);
            e.Property(x => x.SummaryJson).HasColumnName("SummaryJson");
            e.Property(x => x.EventLogJson).HasColumnName("EventLogJson");
        });

        model.Entity<Batch>(e =>
        {
            e.ToTable("Batches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProjectId).HasColumnName("ProjectId").IsRequired();
            e.Property(x => x.Module).HasColumnName("Module").HasConversion(moduleConverter).IsRequired();
            e.Property(x => x.ParentWorkItemId).HasColumnName("ParentWorkItemId");
            e.Property(x => x.Title).HasColumnName("Title").IsRequired().HasDefaultValue(string.Empty);
            e.Property(x => x.Mode).HasColumnName("Mode").HasConversion(modeConverter);
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").IsRequired();
        });

        // ErrorLogs is NOT part of the adopted Alembic schema — it's added by the AddErrorLogs EF
        // migration (applied on boot). Free, DB-only error/warning capture for debugging.
        model.Entity<ErrorLog>(e =>
        {
            e.ToTable("ErrorLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.Source).HasColumnName("Source").IsRequired();
            e.Property(x => x.Level).HasColumnName("Level").IsRequired();
            e.Property(x => x.Category).HasColumnName("Category");
            e.Property(x => x.Message).HasColumnName("Message").IsRequired();
            e.Property(x => x.Exception).HasColumnName("Exception");
            e.Property(x => x.Path).HasColumnName("Path");
            e.Property(x => x.Context).HasColumnName("Context");
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_ErrorLogs_CreatedAt");
        });

        // ── QA Loop tables (AddQaLoopReshape migration) ───────────────────────

        model.Entity<ApiTestSession>(e =>
        {
            e.ToTable("ApiTestSessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProjectId).HasColumnName("ProjectId").IsRequired();
            e.Property(x => x.Module).HasColumnName("Module").IsRequired();
            e.Property(x => x.Repository).HasColumnName("Repository");
            e.Property(x => x.Branch).HasColumnName("Branch");
            e.Property(x => x.WorkItemId).HasColumnName("WorkItemId");
            e.Property(x => x.WorkItemTitle).HasColumnName("WorkItemTitle");
            e.Property(x => x.TestPlanId).HasColumnName("TestPlanId");
            e.Property(x => x.TestSuiteId).HasColumnName("TestSuiteId");
            e.Property(x => x.SourceDirsJson).HasColumnName("SourceDirsJson");
            e.Property(x => x.FrontendModule).HasColumnName("FrontendModule");
            e.Property(x => x.FrontendPath).HasColumnName("FrontendPath");
            e.Property(x => x.Status).HasColumnName("Status").HasConversion<string>().IsRequired();
            e.Property(x => x.InventoryJson).HasColumnName("InventoryJson");
            e.Property(x => x.AnalysisJson).HasColumnName("AnalysisJson");
            e.Property(x => x.EventLogJson).HasColumnName("EventLogJson");
            e.Property(x => x.ErrorMessage).HasColumnName("ErrorMessage");
            e.Property(x => x.AutoHandoff).HasColumnName("AutoHandoff").IsRequired();
            e.Property(x => x.ActivityEnabled).HasColumnName("ActivityEnabled").IsRequired().HasDefaultValue(true);
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").IsRequired();
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt }).HasDatabaseName("ix_ApiTestSessions_ProjectId_CreatedAt");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Drafts).WithOne().HasForeignKey(d => d.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ApiTestDraft>(e =>
        {
            e.ToTable("ApiTestDrafts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.SessionId).HasColumnName("SessionId").IsRequired();
            e.Property(x => x.Level).HasColumnName("Level").HasConversion(testLevelConverter).IsRequired();
            e.Property(x => x.Title).HasColumnName("Title").IsRequired();
            e.Property(x => x.Preconditions).HasColumnName("Preconditions").IsRequired();
            e.Property(x => x.StepsJson).HasColumnName("StepsJson").IsRequired();
            e.Property(x => x.SchemaJson).HasColumnName("SchemaJson");
            e.Property(x => x.SpecCode).HasColumnName("SpecCode").IsRequired();
            e.Property(x => x.SpecFilePath).HasColumnName("SpecFilePath");
            e.Property(x => x.CaseIndex).HasColumnName("CaseIndex");
            e.Property(x => x.Tags).HasColumnName("Tags");
            e.Property(x => x.SourceRoutesJson).HasColumnName("SourceRoutesJson");
            e.Property(x => x.ErrorMessage).HasColumnName("ErrorMessage");
            e.Property(x => x.Status).HasColumnName("Status").HasConversion<string>().IsRequired();
            e.Property(x => x.AdoTestCaseId).HasColumnName("AdoTestCaseId");
            e.Property(x => x.SortOrder).HasColumnName("SortOrder").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").IsRequired();
            e.HasIndex(x => x.SessionId).HasDatabaseName("ix_ApiTestDrafts_SessionId");
        });

        model.Entity<TestRun>(e =>
        {
            e.ToTable("TestRuns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProjectId).HasColumnName("ProjectId").IsRequired();
            e.Property(x => x.Module).HasColumnName("Module");
            e.Property(x => x.LevelFilter).HasColumnName("LevelFilter");
            e.Property(x => x.TagFilter).HasColumnName("TagFilter");
            e.Property(x => x.Profile).HasColumnName("Profile").HasConversion(dmsProfileConverter).IsRequired();
            e.Property(x => x.TestPlanId).HasColumnName("TestPlanId");
            e.Property(x => x.TestSuiteId).HasColumnName("TestSuiteId");
            e.Property(x => x.TriggerSource).HasColumnName("TriggerSource").HasConversion(triggerSourceConverter).IsRequired();
            e.Property(x => x.TriggerPipelineId).HasColumnName("TriggerPipelineId");
            e.Property(x => x.TriggerBuildId).HasColumnName("TriggerBuildId");
            e.Property(x => x.TriggerCommitSha).HasColumnName("TriggerCommitSha");
            e.Property(x => x.ImpactAnalysisJson).HasColumnName("ImpactAnalysisJson");
            e.Property(x => x.Status).HasColumnName("Status").HasConversion<string>().IsRequired();
            e.Property(x => x.Passed).HasColumnName("Passed").IsRequired();
            e.Property(x => x.Failed).HasColumnName("Failed").IsRequired();
            e.Property(x => x.Skipped).HasColumnName("Skipped").IsRequired();
            e.Property(x => x.DurationMs).HasColumnName("DurationMs").IsRequired();
            e.Property(x => x.JunitPath).HasColumnName("JunitPath");
            e.Property(x => x.ReportPath).HasColumnName("ReportPath");
            e.Property(x => x.AdoTestRunId).HasColumnName("AdoTestRunId");
            e.Property(x => x.ErrorMessage).HasColumnName("ErrorMessage");
            e.Property(x => x.StartedAt).HasColumnName("StartedAt");
            e.Property(x => x.FinishedAt).HasColumnName("FinishedAt");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.ActivityEnabled).HasColumnName("ActivityEnabled").IsRequired().HasDefaultValue(true);
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt }).HasDatabaseName("ix_TestRuns_ProjectId_CreatedAt");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Failures).WithOne().HasForeignKey(f => f.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<TestFailure>(e =>
        {
            e.ToTable("TestFailures");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.RunId).HasColumnName("RunId").IsRequired();
            e.Property(x => x.TestTitle).HasColumnName("TestTitle").IsRequired();
            e.Property(x => x.AdoTestCaseId).HasColumnName("AdoTestCaseId");
            e.Property(x => x.Classification).HasColumnName("Classification").HasConversion(failureClassConverter).IsRequired();
            e.Property(x => x.Confidence).HasColumnName("Confidence").IsRequired();
            e.Property(x => x.Reason).HasColumnName("Reason").IsRequired();
            e.Property(x => x.BugDraftJson).HasColumnName("BugDraftJson");
            e.Property(x => x.BugId).HasColumnName("BugId");
            e.Property(x => x.ArtifactPaths).HasColumnName("ArtifactPaths");
            e.HasIndex(x => x.RunId).HasDatabaseName("ix_TestFailures_RunId");
        });

        model.Entity<PipelineWatch>(e =>
        {
            e.ToTable("PipelineWatches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProjectId).HasColumnName("ProjectId").IsRequired();
            e.Property(x => x.AdoPipelineId).HasColumnName("AdoPipelineId").IsRequired();
            e.Property(x => x.AdoPipelineName).HasColumnName("AdoPipelineName").IsRequired();
            e.Property(x => x.Module).HasColumnName("Module").IsRequired();
            e.Property(x => x.LastSeenBuildId).HasColumnName("LastSeenBuildId");
            e.Property(x => x.LastPollAt).HasColumnName("LastPollAt");
            e.Property(x => x.LastPollError).HasColumnName("LastPollError");
            e.Property(x => x.Enabled).HasColumnName("Enabled").IsRequired().HasDefaultValue(true);
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").IsRequired();
            e.HasIndex(x => new { x.ProjectId, x.AdoPipelineId }).IsUnique().HasDatabaseName("ix_PipelineWatches_ProjectId_AdoPipelineId");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── DB Schema Explorer tables (AddSchemaExplorer migration) ───────────
        // All scan-scoped graph rows cascade from their owning scan; a scan cascades from its product,
        // so deleting a product wipes its scans and the whole graph. Table ids inside SchemaRelations
        // are loose indexed refs (no FK constraint) — they point at scan-scoped SchemaTables rows.

        model.Entity<SchemaProduct>(e =>
        {
            e.ToTable("SchemaProducts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasColumnName("Name").IsRequired();
            e.Property(x => x.AdoProject).HasColumnName("AdoProject").IsRequired();
            e.Property(x => x.AdoRepository).HasColumnName("AdoRepository").IsRequired();
            e.Property(x => x.DefaultBranch).HasColumnName("DefaultBranch").IsRequired();
            e.Property(x => x.Description).HasColumnName("Description");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").IsRequired();
        });

        model.Entity<SchemaScan>(e =>
        {
            e.ToTable("SchemaScans");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProductId).HasColumnName("ProductId").IsRequired();
            e.Property(x => x.Status).HasColumnName("Status").HasConversion<string>().IsRequired();
            e.Property(x => x.Phase).HasColumnName("Phase");
            e.Property(x => x.CommitId).HasColumnName("CommitId");
            e.Property(x => x.Branch).HasColumnName("Branch");
            e.Property(x => x.ServiceCount).HasColumnName("ServiceCount").IsRequired();
            e.Property(x => x.TableCount).HasColumnName("TableCount").IsRequired();
            e.Property(x => x.RelationCount).HasColumnName("RelationCount").IsRequired();
            e.Property(x => x.EntityCount).HasColumnName("EntityCount").IsRequired();
            e.Property(x => x.StatsJson).HasColumnName("StatsJson");
            e.Property(x => x.ErrorMessage).HasColumnName("ErrorMessage");
            e.Property(x => x.EventLogJson).HasColumnName("EventLogJson");
            e.Property(x => x.StartedAt).HasColumnName("StartedAt");
            e.Property(x => x.CompletedAt).HasColumnName("CompletedAt");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.HasIndex(x => new { x.ProductId, x.CreatedAt }).HasDatabaseName("ix_SchemaScans_ProductId_CreatedAt");
            e.HasOne<SchemaProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SchemaService>(e =>
        {
            e.ToTable("SchemaServices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ScanId).HasColumnName("ScanId").IsRequired();
            e.Property(x => x.Name).HasColumnName("Name").IsRequired();
            e.Property(x => x.DbContextClass).HasColumnName("DbContextClass");
            e.Property(x => x.SourcePath).HasColumnName("SourcePath");
            e.Property(x => x.SortOrder).HasColumnName("SortOrder").IsRequired();
            e.HasIndex(x => x.ScanId).HasDatabaseName("ix_SchemaServices_ScanId");
            e.HasOne<SchemaScan>().WithMany().HasForeignKey(x => x.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SchemaTable>(e =>
        {
            e.ToTable("SchemaTables");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ScanId).HasColumnName("ScanId").IsRequired();
            e.Property(x => x.ServiceId).HasColumnName("ServiceId");
            e.Property(x => x.Name).HasColumnName("Name").IsRequired();
            e.Property(x => x.SqlSchema).HasColumnName("SqlSchema");
            e.Property(x => x.IsKeyless).HasColumnName("IsKeyless").IsRequired();
            e.Property(x => x.IsExternalRef).HasColumnName("IsExternalRef").IsRequired();
            e.Property(x => x.OwnerServiceName).HasColumnName("OwnerServiceName");
            e.Property(x => x.SourceFile).HasColumnName("SourceFile");
            e.Property(x => x.ColumnsJson).HasColumnName("ColumnsJson");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.HasIndex(x => x.ScanId).HasDatabaseName("ix_SchemaTables_ScanId");
            // ServiceId is a loose indexed ref (no FK) — a table may be unowned/external.
            e.HasIndex(x => x.ServiceId).HasDatabaseName("ix_SchemaTables_ServiceId");
            e.HasOne<SchemaScan>().WithMany().HasForeignKey(x => x.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SchemaRelation>(e =>
        {
            e.ToTable("SchemaRelations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ScanId).HasColumnName("ScanId").IsRequired();
            e.Property(x => x.SourceTableId).HasColumnName("SourceTableId").IsRequired();
            e.Property(x => x.TargetTableId).HasColumnName("TargetTableId").IsRequired();
            e.Property(x => x.SourceColumn).HasColumnName("SourceColumn");
            e.Property(x => x.TargetColumn).HasColumnName("TargetColumn");
            e.Property(x => x.Kind).HasColumnName("Kind").HasConversion<string>().IsRequired();
            e.Property(x => x.Confidence).HasColumnName("Confidence");
            e.Property(x => x.Evidence).HasColumnName("Evidence");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.HasIndex(x => x.ScanId).HasDatabaseName("ix_SchemaRelations_ScanId");
            // SourceTableId/TargetTableId are plain indexed ints — loose refs to SchemaTables (spec §5.3 style).
            e.HasIndex(x => x.SourceTableId).HasDatabaseName("ix_SchemaRelations_SourceTableId");
            e.HasOne<SchemaScan>().WithMany().HasForeignKey(x => x.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SchemaEntity>(e =>
        {
            e.ToTable("SchemaEntities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProductId).HasColumnName("ProductId").IsRequired();
            e.Property(x => x.Key).HasColumnName("Key").IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("DisplayName").IsRequired();
            e.Property(x => x.RootTableName).HasColumnName("RootTableName");
            e.Property(x => x.IdColumn).HasColumnName("IdColumn");
            e.Property(x => x.ScopeColumn).HasColumnName("ScopeColumn");
            e.Property(x => x.DisplayColumnsJson).HasColumnName("DisplayColumnsJson");
            e.Property(x => x.Confidence).HasColumnName("Confidence");
            e.Property(x => x.Source).HasColumnName("Source").IsRequired();
            e.Property(x => x.IsEnabled).HasColumnName("IsEnabled").IsRequired().HasDefaultValue(true);
            e.Property(x => x.SortOrder).HasColumnName("SortOrder").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").IsRequired();
            e.HasIndex(x => new { x.ProductId, x.Key }).IsUnique().HasDatabaseName("ix_SchemaEntities_ProductId_Key");
            e.HasOne<SchemaProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SchemaConnection>(e =>
        {
            e.ToTable("SchemaConnections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("Id").ValueGeneratedOnAdd();
            e.Property(x => x.ProductId).HasColumnName("ProductId").IsRequired();
            e.Property(x => x.Environment).HasColumnName("Environment").IsRequired().HasDefaultValue("Default");
            e.Property(x => x.ServiceName).HasColumnName("ServiceName");
            e.Property(x => x.Server).HasColumnName("Server");
            e.Property(x => x.DbUser).HasColumnName("DbUser");
            e.Property(x => x.DbDatabase).HasColumnName("DbDatabase");
            e.Property(x => x.ConnectionString).HasColumnName("ConnectionString").IsRequired();
            e.Property(x => x.IsDefault).HasColumnName("IsDefault").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").IsRequired();
            e.HasIndex(x => x.ProductId).HasDatabaseName("ix_SchemaConnections_ProductId");
            e.HasOne<SchemaProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
