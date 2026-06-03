namespace QaAutomation.Core.Models;

/// <summary>A workspace tying an ADO project to its knowledgebase and (later) test config.</summary>
public sealed class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AdoOrganization { get; set; } = string.Empty;
    public string AdoProject { get; set; } = string.Empty;

    public List<KnowledgebaseDocument> Documents { get; set; } = [];
}
