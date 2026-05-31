namespace DocuPilot.Services.Tools;

/// <summary>
/// The default <see cref="IToolRegistry"/> — composed from every <see cref="ITool"/> registered in DI.
/// Names are unique (a duplicate registration is a composition bug → throws at construction). Pure
/// in-memory lookup; no per-request state.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly IReadOnlyDictionary<string, ITool> _byName;
    private readonly IReadOnlyList<ToolDefinition> _definitions;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        var map = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!map.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException($"Duplicate tool name '{tool.Name}' registered.");
            }
        }

        _byName = map;
        _definitions = map.Values
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new ToolDefinition(t.Name, t.Description, t.Schema.ToJson()))
            .ToList();
    }

    public IReadOnlyList<ToolDefinition> List() => _definitions;

    public bool TryGet(string name, out ITool tool)
    {
        if (name is not null && _byName.TryGetValue(name, out var found))
        {
            tool = found;
            return true;
        }

        tool = null!;
        return false;
    }
}
