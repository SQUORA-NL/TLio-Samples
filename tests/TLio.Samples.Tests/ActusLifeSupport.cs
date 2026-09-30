using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TLio.Client;
using TLio.Extensions.Looping;
using TLio.Extensions.Math;
using TLio.Extensions.Text;
using TLio.Extensions.TimeDate;
using TLio.Json;

namespace TLio.Samples.Tests;

/// <summary>
/// Shared plumbing for the ACTUS life-insurance tests: the golden data ACTUS-I produced
/// (<c>Resources/oracle/life-oracle.json</c>, see the README next to it), the sample scripts, and a
/// small runner. The scripts are compiled once per name and shared; every execution gets its own
/// input document and context, as TLio requires.
/// </summary>
internal static class ActusLifeSupport
{
    private static readonly string OraclePath =
        Path.Combine(AppContext.BaseDirectory, "Resources", "oracle", "life-oracle.json");

    // Dates stay strings for the same reason as in the PAM tests (culture-dependent rendering).
    private static readonly Lazy<JObject> OracleDoc = new(() =>
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(OraclePath)))
        {
            DateParseHandling = DateParseHandling.None,
        };
        return JObject.Load(reader);
    });

    public static JObject Oracle => OracleDoc.Value;

    /// <summary>The oracle sections of one kind, in file order.</summary>
    public static List<JObject> Sections(string kind) =>
        ((JArray)Oracle["cases"]!).Cast<JObject>().Where(c => (string?)c["kind"] == kind).ToList();

    public static JObject Section(string kind) => Sections(kind).Single();

    public static string ScriptPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "TLio.Sample.Actus.Api")))
            dir = dir.Parent;
        var root = dir?.FullName
            ?? throw new DirectoryNotFoundException("samples/TLio.Sample.Actus.Api is not above the test directory.");
        return Path.Combine(root, "samples", "TLio.Sample.Actus.Api", "Scripts", name);
    }

    private static readonly ConcurrentDictionary<string, CompiledScript<JToken>> Compiled = new();

    public static CompiledScript<JToken> Compile(string scriptName) =>
        Compiled.GetOrAdd(scriptName, name =>
        {
            var options = ParseOptions<JToken>.CreateDefault();
            options.FunctionsProvider.RegisterMath<JToken>();
            options.FunctionsProvider.RegisterText<JToken>();
            options.FunctionsProvider.RegisterTimeDate<JToken>();
            options.CommandsProvider.RegisterLooping<JToken>();
            var engine = new ScriptEngine<JToken>(options.CommandsProvider, options.FunctionsProvider);
            var adapter = JsonExecutionContext.CreateDefault().NodeAdapter;
            return engine.Compile(File.ReadAllText(ScriptPath(name)), adapter);
        });

    /// <summary>Runs a script on a copy of <paramref name="input"/>. Data is the resulting document.</summary>
    public static (JToken? Data, string? Error) Run(string scriptName, JToken input)
    {
        try
        {
            var context = JsonExecutionContext.CreateDefault();
            var result = Compile(scriptName).Execute(input.DeepClone(), context);
            if (!result.Success)
            {
                var reasons = string.Join("; ", context.GetLogEntries()
                    .Where(e => e.Level.ToString() == "Error").Take(3).Select(e => e.Message));
                return (result.Data, $"script failed: {reasons}");
            }
            return (result.Data, null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static double D(JToken? t) => t is null ? double.NaN : (double)t;

    /// <summary>The 64 contracts of the oracle by name, in the order of every projection section.</summary>
    public static IReadOnlyList<string> ContractOrder =>
        ((JArray)Oracle["contractOrder"]!).Select(t => (string)t!).ToList();

    /// <summary>A contract snapshot as the oracle records it, looked up by name in the factors section.</summary>
    public static JObject Contract(string name) =>
        (JObject)((JArray)Section("factors")["items"]!).Cast<JObject>()
            .Single(i => (string?)i["contract"]!["name"] == name)["contract"]!.DeepClone();

    /// <summary>Per-area result lines, printed once by the test that owns them.</summary>
    public static string Table(string title, IEnumerable<(string Area, string Result, string Detail)> rows)
    {
        var list = rows.ToList();
        var w0 = System.Math.Max(4, list.Max(r => r.Area.Length));
        var w1 = System.Math.Max(6, list.Max(r => r.Result.Length));
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(title);
        foreach (var (a, r, d) in list)
            sb.AppendLine($"  {a.PadRight(w0)}  {r.PadRight(w1)}  {d}");
        return sb.ToString();
    }
}
