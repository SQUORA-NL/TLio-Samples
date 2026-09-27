using System.Diagnostics.CodeAnalysis;

namespace TLio.Sample.Api.Registry;

internal interface IScriptRegistry
{
    void Add(ScriptRegistryEntry entry);
    bool TryGet(string slug, [MaybeNullWhen(false)] out ScriptRegistryEntry entry);
    IReadOnlyList<ScriptRegistryEntry> List();
    bool Delete(string slug);
}
