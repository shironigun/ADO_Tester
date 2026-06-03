namespace QaAutomation.Core.Configuration;

/// <summary>Local storage locations. DataDirectory holds the SQLite DB and uploaded files.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root data folder (absolute or relative to the API working dir). Default: "data".</summary>
    public string DataDirectory { get; set; } = "data";

    public string DatabaseFileName { get; set; } = "qa.db";

    public string KnowledgebaseFolderName { get; set; } = "kb";
}
