using System.Text.RegularExpressions;

namespace QaAutomation.Infrastructure.SchemaExplorer;

/// <summary>
/// Deterministic (regex) parser for the EF Core patterns the product actually uses (Phase B):
/// <c>DbSet&lt;T&gt;</c> declarations, <c>[Table(nameof(X)|"lit", Schema = Schemas.Y|"lit")]</c>,
/// <c>[Key]</c>, <c>[ForeignKey(nameof(col))]</c> navigation properties, and the
/// <c>OnModelCreating</c> calls <c>.HasNoKey()</c> / <c>.HasKey(x =&gt; new { ... })</c>.
/// It is intentionally scoped to those patterns — anything exotic is left for the Phase C AI pass.
/// Pure/static and side-effect free so it's trivially unit-testable.
/// </summary>
public static partial class EfSchemaParser
{
    /// <summary>C# scalar/value types that map to columns (everything else PascalCase is a candidate
    /// navigation until proven otherwise at assembly time). Nullable "?" and namespaces are stripped first.</summary>
    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal)
    {
        "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort", "bool", "string",
        "decimal", "double", "float", "char", "DateTime", "DateTimeOffset", "TimeSpan", "Guid",
        "object", "Int32", "Int64", "Boolean", "String", "Decimal", "Double", "Single", "byte[]",
    };

    public sealed record ParsedProp(string Name, string RawType, bool IsKey, string? FkColumn);

    public sealed record ParsedClass(string ClassName, string? TableName, string? Schema, List<ParsedProp> Props);

    public sealed record DbContextInfo(
        string ClassName,
        List<string> DbSetTypes,
        HashSet<string> HasNoKey,
        Dictionary<string, List<string>> CompositeKeys);

    // ── DbContext ────────────────────────────────────────────────────────────────

    /// <summary>True if the file declares a class deriving from DbContext.</summary>
    public static bool LooksLikeDbContext(string content) => DbContextDecl().IsMatch(content);

    public static DbContextInfo ParseDbContext(string content)
    {
        var className = DbContextDecl().Match(content) is { Success: true } m ? m.Groups[1].Value : "DbContext";

        // DbSet/Entity type args may be namespace-qualified (e.g. DbSet<Data.Invoice>) — normalize to the
        // last segment so they match the parsed class names (which are unqualified).
        var dbSets = new List<string>();
        foreach (Match ds in DbSetDecl().Matches(content))
            dbSets.Add(LastSegment(ds.Groups[1].Value));

        var noKey = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match nk in HasNoKeyCall().Matches(content))
            noKey.Add(LastSegment(nk.Groups[1].Value));

        var composite = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match hk in HasKeyComposite().Matches(content))
        {
            var type = LastSegment(hk.Groups[1].Value);
            var cols = LambdaMembers().Matches(hk.Groups[2].Value).Select(x => x.Groups[1].Value).ToList();
            if (cols.Count > 0) composite[type] = cols;
        }

        return new DbContextInfo(className, dbSets.Distinct().ToList(), noKey, composite);
    }

    // ── Entity / POCO classes ──────────────────────────────────────────────────────

    /// <summary>Parse every class in a file into name + optional [Table] mapping + properties. Properties
    /// are captured raw (type + name + [Key]/[ForeignKey]); column-vs-relation is decided at assembly
    /// once the full set of known tables is known.</summary>
    public static List<ParsedClass> ParseClasses(string content)
    {
        var result = new List<ParsedClass>();
        var pendingAttrs = new List<string>();
        ParsedClass? current = null;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // Accumulate attributes seen on this line (there may be several, e.g. "[Key] public ...").
            foreach (Match a in AttrToken().Matches(line)) pendingAttrs.Add(a.Groups[1].Value);

            var classMatch = ClassDecl().Match(line);
            if (classMatch.Success)
            {
                var (table, schema) = ExtractTable(pendingAttrs);
                current = new ParsedClass(classMatch.Groups[1].Value, table, schema, []);
                result.Add(current);
                pendingAttrs.Clear();
                continue;
            }

            var propMatch = PropDecl().Match(line);
            if (current is not null && propMatch.Success)
            {
                var type = propMatch.Groups[1].Value.Trim();
                var name = propMatch.Groups[2].Value;
                if (!type.StartsWith("DbSet"))   // skip DbSet props if this class is the context itself
                {
                    var isKey = pendingAttrs.Any(a => a == "Key" || a.StartsWith("Key("));
                    var fk = pendingAttrs.Select(ExtractForeignKey).FirstOrDefault(x => x is not null);
                    current.Props.Add(new ParsedProp(name, type, isKey, fk));
                }
                pendingAttrs.Clear();
                continue;
            }

            // A line that's neither an attribute-only line nor a member: drop stale attributes so they
            // can't leak onto an unrelated later member. (Attribute-only lines matched above and continue.)
            if (!line.StartsWith('[')) pendingAttrs.Clear();
        }

        return result;
    }

    /// <summary>List&lt;T&gt;/collection element type (last segment), or null if not a collection.</summary>
    public static string? CollectionElement(string rawType)
    {
        var m = CollectionType().Match(rawType);
        return m.Success ? LastSegment(m.Groups[1].Value) : null;
    }

    /// <summary>True if a (non-collection) type maps to a scalar column rather than a navigation.</summary>
    public static bool IsScalar(string rawType)
    {
        var t = rawType.Trim().TrimEnd('?');
        var lastDot = t.LastIndexOf('.');
        if (lastDot >= 0) t = t[(lastDot + 1)..];
        return Primitives.Contains(t);
    }

    private static (string? table, string? schema) ExtractTable(List<string> attrs)
    {
        var tableAttr = attrs.LastOrDefault(a => a.StartsWith("Table("));
        if (tableAttr is null) return (null, null);
        var m = TableArgs().Match(tableAttr);
        if (!m.Success) return (null, null);
        var name = m.Groups[1].Success ? m.Groups[1].Value : (m.Groups[2].Success ? m.Groups[2].Value : null);
        string? schema = null;
        if (m.Groups[3].Success) schema = LastSegment(m.Groups[3].Value);   // Schemas.Customers → Customers
        else if (m.Groups[4].Success) schema = m.Groups[4].Value;
        return (name, schema);
    }

    private static string? ExtractForeignKey(string attr)
    {
        var m = ForeignKeyArg().Match(attr);
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    // ponytail: don't resolve the Schemas.* constant, just take the last identifier segment.
    private static string LastSegment(string s)
    {
        s = s.Trim();
        var i = s.LastIndexOf('.');
        return i >= 0 ? s[(i + 1)..] : s;
    }

    [GeneratedRegex(@"class\s+(\w+)\s*(?::[^{]*)?\bDbContext\b")]
    private static partial Regex DbContextDecl();

    [GeneratedRegex(@"DbSet<\s*([\w.]+)\s*>\s+\w+\s*\{\s*get;")]
    private static partial Regex DbSetDecl();

    [GeneratedRegex(@"\.Entity<\s*([\w.]+)\s*>\(\)\s*\.HasNoKey\(\)")]
    private static partial Regex HasNoKeyCall();

    [GeneratedRegex(@"\.Entity<\s*([\w.]+)\s*>\(\)\s*\.HasKey\(\s*\w+\s*=>\s*new\s*\{([^}]+)\}")]
    private static partial Regex HasKeyComposite();

    [GeneratedRegex(@"\w+\.(\w+)")]
    private static partial Regex LambdaMembers();

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex AttrToken();

    [GeneratedRegex(@"(?:public|internal|protected)\s+(?:sealed\s+|abstract\s+|partial\s+|static\s+)*class\s+(\w+)")]
    private static partial Regex ClassDecl();

    [GeneratedRegex(@"public\s+([A-Za-z0-9_<>\?,\.\[\]]+?)\s+(\w+)\s*\{\s*get;")]
    private static partial Regex PropDecl();

    [GeneratedRegex(@"^(?:List|ICollection|IEnumerable|IList|HashSet|Collection)<\s*([\w.]+)\s*>")]
    private static partial Regex CollectionType();

    [GeneratedRegex("""Table\(\s*(?:nameof\(\s*(\w+)\s*\)|"([^"]+)")\s*(?:,\s*Schema\s*=\s*(?:([\w.]+)|"([^"]+)"))?""")]
    private static partial Regex TableArgs();

    [GeneratedRegex(@"ForeignKey\(\s*(?:nameof\(\s*(\w+)\s*\)|""(\w+)"")")]
    private static partial Regex ForeignKeyArg();
}
