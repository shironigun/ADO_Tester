namespace QaAutomation.Core.Contracts;

/// <summary>
/// Request to extract a module's knowledgebase from product source (Track A — hackathon KB foundation).
/// Reads from a local repo clone (<see cref="SourceRoot"/>); the productized path can later swap to the
/// ADO Git client without changing this contract.
/// </summary>
public sealed record KbExtractionRequest
{
    /// <summary>Module key, e.g. "deals". Drives output file names and the prompts.</summary>
    public string Module { get; init; } = "";

    /// <summary>Absolute path to the product repo root to read source from (the local clone).</summary>
    public string SourceRoot { get; init; } = "";

    /// <summary>Flow names to extract — one KB doc each, e.g. ["Create Deal", "Mark Deal as Lost"].</summary>
    public List<string> Flows { get; init; } = [];

    /// <summary>Repo-relative dirs to read. Null → sensible defaults for the module (component + e2e dirs).</summary>
    public List<string>? SourceDirs { get; init; }

    /// <summary>File extensions to include. Null → [".ts", ".tsx"].</summary>
    public List<string>? Extensions { get; init; }

    /// <summary>Per-file truncation cap (chars) so one large file can't dominate the bundle.</summary>
    public int MaxFileChars { get; init; } = 8_000;

    /// <summary>Whole-bundle cap (chars) for the source passed to the LLM on each call.</summary>
    public int MaxBundleChars { get; init; } = 120_000;
}

/// <summary>Result of a KB extraction run — the produced (stored + indexed) documents.</summary>
public sealed record KbExtractionResult
{
    public string Module { get; init; } = "";
    public int FilesRead { get; init; }
    public int BundleChars { get; init; }
    public List<KbDocResult> Docs { get; init; } = [];
}

/// <summary>One produced KB document, stored on disk and indexed into the FTS knowledgebase.</summary>
public sealed record KbDocResult
{
    public string FileName { get; init; } = "";
    public string Kind { get; init; } = "";   // "flow" | "rules"
    public int CharCount { get; init; }
    public int DocumentId { get; init; }       // KnowledgebaseDocument id
}
