namespace QaAutomation.Core.Models;

/// <summary>A workspace tying an ADO org/project to its knowledgebase + sessions. Seeded as id=1
/// (Name "Your Project"). Table: Projects.</summary>
public sealed class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AdoOrganization { get; set; } = string.Empty;
    public string AdoProject { get; set; } = string.Empty;
}
