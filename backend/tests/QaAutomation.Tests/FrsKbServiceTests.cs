using Microsoft.Extensions.Logging.Abstractions;
using QaAutomation.Core.Abstractions;
using QaAutomation.Core.Models;
using QaAutomation.Infrastructure.QaLoop;

namespace QaAutomation.Tests;

/// <summary>Unit tests for <see cref="FrsKbService"/> — FRS KB slug resolution, atlas YAML parsing,
/// RULES section extraction, and graceful degradation when files are missing or malformed.</summary>
public sealed class FrsKbServiceTests : IDisposable
{
    private const int ProjectId = 1;
    private readonly FakeKnowledgebaseService _kb = new();
    private readonly FrsKbService _svc;
    private readonly List<string> _tempFiles = [];

    public FrsKbServiceTests()
    {
        _svc = new FrsKbService(_kb, NullLogger<FrsKbService>.Instance);
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }

    // ── ListSlugsAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListSlugsAsync_returns_slugs_from_modules_folder_docs()
    {
        SeedModule("messaging.md", "# Messaging\nbrief");
        SeedModule("inventory.md", "# Inventory\nbrief");
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "messaging.md",
            FolderPath = "atlas",
            StoredPath = WriteTemp("# Atlas"),
            UploadedAt = DateTime.UtcNow,
        });

        var slugs = await _svc.ListSlugsAsync(ProjectId, CancellationToken.None);

        Assert.Equal(["inventory", "messaging"], slugs);
    }

    // ── ResolveModuleAsync — matching ─────────────────────────────────────────

    [Fact]
    public async Task ResolveModuleAsync_exact_slug_match()
    {
        SeedMessagingBundle();

        var ctx = await _svc.ResolveModuleAsync("messaging", ProjectId, CancellationToken.None);

        Assert.NotNull(ctx);
        Assert.Equal("messaging", ctx!.Slug);
        Assert.Contains("# Messaging", ctx.ModuleMarkdown);
        Assert.Equal(["src/components/messaging/**"], ctx.FrontendPathIncludes);
        Assert.Equal(["src/api/Messaging/**"], ctx.BackendPathIncludes);
    }

    [Fact]
    public async Task ResolveModuleAsync_alias_match_via_atlas_frontmatter()
    {
        SeedMessagingBundle();

        var ctx = await _svc.ResolveModuleAsync("inbox", ProjectId, CancellationToken.None);

        Assert.NotNull(ctx);
        Assert.Equal("messaging", ctx!.Slug);
    }

    [Fact]
    public async Task ResolveModuleAsync_keyword_match_via_keywords_exclusive()
    {
        SeedMessagingBundle();

        var ctx = await _svc.ResolveModuleAsync("team-messaging-ui", ProjectId, CancellationToken.None);

        Assert.NotNull(ctx);
        Assert.Equal("messaging", ctx!.Slug);
        Assert.Contains("messaging", ctx.KeywordsExclusive, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveModuleAsync_returns_null_when_kb_empty()
    {
        var ctx = await _svc.ResolveModuleAsync("messaging", ProjectId, CancellationToken.None);
        Assert.Null(ctx);
    }

    [Fact]
    public async Task ResolveModuleAsync_returns_null_when_no_modules()
    {
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "RULES.md",
            FolderPath = "",
            StoredPath = WriteTemp("## other\n- rule"),
            UploadedAt = DateTime.UtcNow,
        });

        var ctx = await _svc.ResolveModuleAsync("messaging", ProjectId, CancellationToken.None);
        Assert.Null(ctx);
    }

    [Fact]
    public async Task ResolveModuleAsync_returns_null_when_no_slug_matches()
    {
        SeedMessagingBundle();

        var ctx = await _svc.ResolveModuleAsync("nonexistent-module", ProjectId, CancellationToken.None);
        Assert.Null(ctx);
    }

    // ── RULES.md section extraction ───────────────────────────────────────────

    [Fact]
    public async Task ResolveModuleAsync_extracts_RULES_section_for_slug()
    {
        SeedMessagingBundle(rulesMarkdown: "## messaging\n- rule one\n## other\n- other rule");

        var ctx = await _svc.ResolveModuleAsync("messaging", ProjectId, CancellationToken.None);

        Assert.NotNull(ctx);
        Assert.Contains("## messaging", ctx!.RulesMarkdown);
        Assert.Contains("- rule one", ctx.RulesMarkdown);
        Assert.DoesNotContain("other rule", ctx.RulesMarkdown);
    }

    // ── Graceful degradation ──────────────────────────────────────────────────

    [Fact]
    public async Task ResolveModuleAsync_returns_null_when_module_StoredPath_missing()
    {
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "messaging.md",
            FolderPath = "modules",
            StoredPath = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.md"),
            UploadedAt = DateTime.UtcNow,
        });

        var ctx = await _svc.ResolveModuleAsync("messaging", ProjectId, CancellationToken.None);
        Assert.Null(ctx);
    }

    [Fact]
    public async Task ResolveModuleAsync_graceful_skip_malformed_atlas_yaml_does_not_throw()
    {
        var modulePath = WriteTemp("# Messaging\nbrief");
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "messaging.md",
            FolderPath = "modules",
            StoredPath = modulePath,
            UploadedAt = DateTime.UtcNow,
        });
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "messaging.md",
            FolderPath = "atlas",
            StoredPath = WriteTemp("---\n: [ broken yaml\n---\n# body"),
            UploadedAt = DateTime.UtcNow,
        });

        var ctx = await _svc.ResolveModuleAsync("messaging", ProjectId, CancellationToken.None);

        Assert.NotNull(ctx);
        Assert.Equal("messaging", ctx!.Slug);
        Assert.Empty(ctx.FrontendPathIncludes);
        Assert.Empty(ctx.BackendPathIncludes);
        Assert.Equal("[]", ctx.FeaturesJson);
    }

    [Fact]
    public async Task ResolveModuleAsync_alias_skips_doc_when_atlas_StoredPath_missing()
    {
        var modulePath = WriteTemp("# Other\nbrief");
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "other.md",
            FolderPath = "modules",
            StoredPath = modulePath,
            UploadedAt = DateTime.UtcNow,
        });
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "other.md",
            FolderPath = "atlas",
            StoredPath = Path.Combine(Path.GetTempPath(), $"missing_atlas_{Guid.NewGuid():N}.md"),
            UploadedAt = DateTime.UtcNow,
        });

        var ctx = await _svc.ResolveModuleAsync("alias-only", ProjectId, CancellationToken.None);
        Assert.Null(ctx);
    }

    // ── Dedup ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveModuleAsync_dedup_latest_UploadedAt_wins_for_duplicate_FileName()
    {
        var olderPath = WriteTemp("# Messaging\nold brief");
        var newerPath = WriteTemp("# Messaging\nnew brief");
        var atlasPath = WriteTemp("""
            ---
            slug: messaging
            aliases:
              - messages
            keywords_exclusive:
              - messaging
            frontend_path_includes:
              - src/components/messaging/**
            backend_path_includes:
              - src/api/Messaging/**
            ---
            # Atlas body
            """);

        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "messaging.md",
            FolderPath = "modules",
            StoredPath = olderPath,
            UploadedAt = DateTime.UtcNow.AddDays(-2),
        });
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "messaging.md",
            FolderPath = "modules",
            StoredPath = newerPath,
            UploadedAt = DateTime.UtcNow,
        });
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "messaging.md",
            FolderPath = "atlas",
            StoredPath = atlasPath,
            UploadedAt = DateTime.UtcNow,
        });

        var ctx = await _svc.ResolveModuleAsync("messaging", ProjectId, CancellationToken.None);

        Assert.NotNull(ctx);
        Assert.Contains("new brief", ctx!.ModuleMarkdown);
        Assert.DoesNotContain("old brief", ctx.ModuleMarkdown);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void SeedMessagingBundle(string? rulesMarkdown = null)
    {
        var modulePath = WriteTemp("# Messaging\nbrief");
        var atlasPath = WriteTemp("""
            ---
            slug: messaging
            aliases:
              - messages
              - inbox
            keywords_exclusive:
              - messaging
            frontend_path_includes:
              - src/components/messaging/**
            backend_path_includes:
              - src/api/Messaging/**
            ---
            # Atlas body
            """);

        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "messaging.md",
            FolderPath = "modules",
            StoredPath = modulePath,
            UploadedAt = DateTime.UtcNow,
        });
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = "messaging.md",
            FolderPath = "atlas",
            StoredPath = atlasPath,
            UploadedAt = DateTime.UtcNow,
        });

        if (rulesMarkdown != null)
        {
            _kb.Add(new KnowledgebaseDocument
            {
                ProjectId = ProjectId,
                FileName = "RULES.md",
                FolderPath = "",
                StoredPath = WriteTemp(rulesMarkdown),
                UploadedAt = DateTime.UtcNow,
            });
        }
    }

    private void SeedModule(string fileName, string markdown)
    {
        _kb.Add(new KnowledgebaseDocument
        {
            ProjectId = ProjectId,
            FileName = fileName,
            FolderPath = "modules",
            StoredPath = WriteTemp(markdown),
            UploadedAt = DateTime.UtcNow,
        });
    }

    private string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"frs_kb_{Guid.NewGuid():N}.md");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private sealed class FakeKnowledgebaseService : IKnowledgebaseService
    {
        private readonly List<KnowledgebaseDocument> _docs = [];
        private int _nextId = 1;

        public void Add(KnowledgebaseDocument doc)
        {
            if (doc.Id == 0)
                doc.Id = _nextId++;
            _docs.Add(doc);
        }

        public Task<IReadOnlyList<KnowledgebaseDocument>> GetDocumentsByFolderAsync(
            int projectId, string folderPath, CancellationToken cancellationToken = default)
        {
            var result = _docs
                .Where(d => d.ProjectId == projectId && FolderMatches(d.FolderPath, folderPath))
                .ToList();
            return Task.FromResult<IReadOnlyList<KnowledgebaseDocument>>(result);
        }

        public Task<KnowledgebaseDocument?> GetDocumentByPathAsync(
            int projectId, string folderPath, string fileName, CancellationToken cancellationToken = default)
        {
            var match = _docs
                .Where(d => d.ProjectId == projectId
                    && string.Equals(NormalizeFolder(d.FolderPath), folderPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.UploadedAt)
                .FirstOrDefault();
            return Task.FromResult(match);
        }

        public Task<KnowledgebaseDocument> UploadAsync(
            int projectId, string fileName, string markdown,
            string? folderPath = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<KnowledgebaseDocument>> ListAsync(
            int projectId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DeleteAsync(int documentId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<KnowledgebaseHit>> SearchAsync(
            int projectId, string query, int topK = 8, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        private static string NormalizeFolder(string? folderPath) => folderPath ?? "";

        private static bool FolderMatches(string? docFolder, string queryFolder)
        {
            var folder = NormalizeFolder(docFolder);
            if (string.IsNullOrEmpty(queryFolder))
                return string.IsNullOrEmpty(folder);

            return string.Equals(folder, queryFolder, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(queryFolder + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
