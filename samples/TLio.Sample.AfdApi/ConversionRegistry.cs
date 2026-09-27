using TLio.FormatConverter;

namespace TLio.Sample.AfdApi;

/// <summary>
/// Compiles each of the three AFD scripts once, at startup, and keeps the compiled form around
/// for every request.
/// </summary>
/// <remarks>
/// The scripts here are several MB — parsing one of them back into commands on every request
/// used to dominate the cost of a conversion (~130ms out of ~220ms measured for
/// afdshort-to-afd2). <see cref="MultiFormatScriptRunner.Compile"/> parses once; each request
/// then only pays for cloning the command tree (near-zero — see
/// <see cref="TLio.Client.CompiledScript{TNode}.CreateExecutable"/>) and actually running it.
/// </remarks>
internal sealed class ConversionRegistry
{
    private readonly Dictionary<string, (CompiledMultiFormatScript Script, string InputFormatId)> _compiled = new();

    public static ConversionRegistry Create(string scriptsDir, MultiFormatScriptRunner runner)
    {
        var registry = new ConversionRegistry();
        registry.Add(runner, "afd1-to-afd2", "xml", Path.Combine(scriptsDir, "afd1-to-afd2.tlio.json"));
        registry.Add(runner, "afd1-to-afdshort", "xml", Path.Combine(scriptsDir, "afd1-to-afdshort.tlio.json"));
        registry.Add(runner, "afdshort-to-afd2", "json", Path.Combine(scriptsDir, "afdshort-to-afd2.tlio.json"));
        return registry;
    }

    /// <summary>
    /// Runs each compiled script once before the server takes traffic. .NET JITs a method on its
    /// first call, not at compile time, so without this the *first* real request to each
    /// direction still pays a one-off tiering cost the ones after it don't — this moves that cost
    /// to startup instead of onto whichever client happens to send the first request.
    /// </summary>
    public void WarmUp(string direction, string sampleInputDocument) =>
        Run(direction, sampleInputDocument);

    private void Add(MultiFormatScriptRunner runner, string direction, string inputFormatId, string scriptPath)
    {
        var scriptText = File.ReadAllText(scriptPath);
        _compiled[direction] = (runner.Compile(inputFormatId, scriptText), inputFormatId);
    }

    public MultiFormatScriptResult Run(string direction, string inputDocument) =>
        _compiled[direction].Script.Run(inputDocument);
}
