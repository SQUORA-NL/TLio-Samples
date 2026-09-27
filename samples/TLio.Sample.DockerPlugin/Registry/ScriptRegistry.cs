using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace TLio.Sample.DockerPlugin.Registry;

internal sealed class ScriptRegistry : IScriptRegistry
{
    private readonly ConcurrentDictionary<string, ScriptRegistryEntry> _store = new(StringComparer.OrdinalIgnoreCase);

    public void Add(ScriptRegistryEntry entry) =>
        _store.AddOrUpdate(entry.Slug, entry, (_, _) => entry);

    public bool TryGet(string slug, [MaybeNullWhen(false)] out ScriptRegistryEntry entry) =>
        _store.TryGetValue(slug, out entry);

    public IReadOnlyList<ScriptRegistryEntry> List() =>
        _store.Values.ToList();

    public bool Delete(string slug) =>
        _store.TryRemove(slug, out _);
}
