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

    protected override void OnModelCreating(ModelBuilder model)
    {
        // BatchModule/BatchMode store their kebab/lowercase wire value (NOT the C# member name).
        var moduleConverter = new ValueConverter<BatchModule, string>(
            v => v.ToWire(), v => BatchEnumExtensions.ToBatchModule(v));
        var modeConverter = new ValueConverter<BatchMode?, string?>(
            v => v == null ? null : v.Value.ToWire(),
            v => v == null ? null : BatchEnumExtensions.ToBatchMode(v));

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
            e.Property(x => x.BatchId).HasColumnName("BatchId");
            e.Property(x => x.CreatedAt).HasColumnName("CreatedAt").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("UpdatedAt").IsRequired();
            // BatchId is a plain indexed int — NO FK constraint in the live DB (spec §5.3).
            e.HasIndex(x => x.BatchId).HasDatabaseName("ix_TestSessions_BatchId");
            e.HasMany(x => x.Drafts).WithOne().HasForeignKey(d => d.SessionId).OnDelete(DeleteBehavior.Cascade);
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
    }
}
