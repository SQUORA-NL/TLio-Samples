using System.Diagnostics;
using Newtonsoft.Json.Linq;
using TLio.Client;
using TLio.Core.Contracts;
using TLio.Core.Models;
using TLio.Json;

namespace TLio.Sample.AzureDemo;

/// <summary>
/// Wraps a single, shared <see cref="ScriptEngine{TNode}"/> for JSON documents. Registered as a
/// singleton in <c>Program.cs</c> — the engine's command/function registries are immutable after
/// construction, so one instance safely serves every concurrent request.
/// </summary>
public sealed class TlioTransformer
{
    private readonly ScriptEngine<JToken> _engine;

    public TlioTransformer()
    {
        var options = ParseOptions<JToken>.CreateDefault();
        _engine = new ScriptEngine<JToken>(options.CommandsProvider, options.FunctionsProvider);
    }

    /// <param name="documentJson">The input document, as JSON text.</param>
    /// <param name="commandsJson">A TLio script — a JSON array of command objects — as text.</param>
    public TlioTransformResult Transform(string documentJson, string commandsJson)
    {
        var context = JsonExecutionContext.CreateDefault();

        JToken document;
        try
        {
            document = context.NodeAdapter.Parse(documentJson);
        }
        catch (Exception ex)
        {
            return new TlioTransformResult(false, JValue.CreateNull(), new[] { $"Could not parse 'document' as JSON: {ex.Message}" });
        }

        var result = _engine.Execute(commandsJson, document, context);
        var log = context.GetLogEntries()
            .Select(entry => $"[{entry.Level}] {entry.Group}: {entry.Message}")
            .ToArray();

        return new TlioTransformResult(result.Success, result.Data, log);
    }

    /// <summary>
    /// Runs the same script <paramref name="iterations"/> times two ways, to make the cost of
    /// re-parsing visible: once the naive way <see cref="Transform"/> always does — parsing
    /// <paramref name="commandsJson"/> fresh via <see cref="ScriptEngine{TNode}.Execute(string, TNode, IExecutionContext{TNode})"/>
    /// on every call — and once via <see cref="ScriptEngine{TNode}.Compile(string, INodeAdapter{TNode})"/>,
    /// which parses it exactly once and only clones/executes commands after that.
    ///
    /// Each iteration gets its own document clone and execution context on both sides, so the
    /// only thing the two loops actually measure differently is the repeated parse.
    /// </summary>
    public TlioBenchmarkResult Benchmark(string documentJson, string commandsJson, int iterations)
    {
        var parseContext = JsonExecutionContext.CreateDefault();

        JToken baseDocument;
        try
        {
            baseDocument = parseContext.NodeAdapter.Parse(documentJson);
        }
        catch (Exception ex)
        {
            return TlioBenchmarkResult.Failed($"Could not parse 'document' as JSON: {ex.Message}");
        }

        TLioExecutionResult<JToken>? naiveLast = null;
        var naiveMs = TimeMs(() =>
        {
            for (var i = 0; i < iterations; i++)
                naiveLast = _engine.Execute(commandsJson, baseDocument.DeepClone(), JsonExecutionContext.CreateDefault());
        });

        CompiledScript<JToken>? compiled = null;
        var compileMs = TimeMs(() => compiled = _engine.Compile(commandsJson, parseContext.NodeAdapter));

        TLioExecutionResult<JToken>? compiledLast = null;
        IExecutionContext<JToken>? lastContext = null;
        var compiledExecuteMs = TimeMs(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                lastContext = JsonExecutionContext.CreateDefault();
                compiledLast = compiled!.Execute(baseDocument.DeepClone(), lastContext);
            }
        });

        var log = lastContext!.GetLogEntries()
            .Select(entry => $"[{entry.Level}] {entry.Group}: {entry.Message}")
            .ToArray();

        return new TlioBenchmarkResult(
            naiveLast!.Success && compiledLast!.Success,
            iterations, naiveMs, compileMs, compiledExecuteMs,
            compiledLast!.Data, log);
    }

    private static double TimeMs(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        return stopwatch.Elapsed.TotalMilliseconds;
    }
}

public sealed record TlioTransformResult(bool Success, JToken Result, IReadOnlyList<string> Log);

public sealed record TlioBenchmarkResult(
    bool Success, int Iterations, double NaiveMs, double CompileMs, double CompiledExecuteMs,
    JToken Result, IReadOnlyList<string> Log)
{
    public double CompiledTotalMs => CompileMs + CompiledExecuteMs;
    public double Speedup => CompiledTotalMs > 0 ? NaiveMs / CompiledTotalMs : 0;

    public static TlioBenchmarkResult Failed(string message) =>
        new(false, 0, 0, 0, 0, JValue.CreateNull(), new[] { message });
}
