using System.Text.Json;
using TLio.FormatConverter.Json;
using TLio.FormatConverter;
using TLio.FormatConverter.Xml;
using TLio.FormatConverter.Yaml;
using TLio.Client;
using TLio.Core.Models;
using TLio.Extensions.ETL;
using TLio.Extensions.Math;
using TLio.Extensions.Text;
using TLio.Extensions.TimeDate;
using TLio.Json;
using TLio.Xml;
using TLio.Yaml;

// ── Argument parsing ─────────────────────────────────────────────────────────

string? inputPath  = null;
string? scriptPath = null;
string? outputPath = null;
string? batchDir   = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input"  when i + 1 < args.Length: inputPath  = args[++i]; break;
        case "--script" when i + 1 < args.Length: scriptPath = args[++i]; break;
        case "--output" when i + 1 < args.Length: outputPath = args[++i]; break;
        case "--batch"  when i + 1 < args.Length: batchDir   = args[++i]; break;
        case "--help":
            Console.WriteLine("""
                TLio.Sample.Cli — transform a data file using a TLio script

                Usage:
                  TLio.Sample.Cli --input <path> --script <path> [--output <path>]
                  TLio.Sample.Cli --batch <fixture-dir>

                Arguments:
                  --input  <path>   Path to the input data file (.json, .xml, .yaml, .yml)
                  --script <path>   Path to the TLio script file (.json, .xml, .yaml, .yml)
                  --output <path>   (optional) Write transformed output to this file instead of stdout
                  --batch  <path>   Process all fixture .json files in directory (fast, single process)
                  --help            Show this help and exit

                The script's notation is taken from its file extension, falling back to the
                shape of the text. It is independent of the input format — an XML script can
                transform a JSON document — but the paths inside the script must be written in
                the input format's path language.

                Batch mode reads fixture files with {input, script, result} format,
                runs each through the engine, and outputs JSON lines:
                  {"file":"name.json","status":"OK","result":{...}}
                  {"file":"name.json","status":"FAIL","error":"...","log":["..."]}

                Exit codes:
                  0  Transformation succeeded (or batch completed)
                  1  Input file not found
                  2  Script file not found
                  3  Input file could not be parsed
                  4  Script file could not be parsed
                  5  Transformation execution failed
                 10  Unexpected error
                """);
            return 0;
    }
}

// ── Batch mode ──────────────────────────────────────────────────────────────

if (!string.IsNullOrEmpty(batchDir))
{
    if (!Directory.Exists(batchDir))
    {
        Console.Error.WriteLine($"Error: Batch directory not found: {batchDir}");
        return 1;
    }

    // Pre-create the engine and options ONCE (the whole point of batch mode)
    var jsonContext = JsonExecutionContext.CreateDefault();
    var jsonAdapter = jsonContext.NodeAdapter;
    var jsonOptions = ParseOptions<Newtonsoft.Json.Linq.JToken>.CreateDefault();
    jsonOptions.FunctionsProvider.RegisterMath<Newtonsoft.Json.Linq.JToken>();
    jsonOptions.FunctionsProvider.RegisterText<Newtonsoft.Json.Linq.JToken>();
    jsonOptions.FunctionsProvider.RegisterTimeDate<Newtonsoft.Json.Linq.JToken>();
    jsonOptions.CommandsProvider.RegisterETL<Newtonsoft.Json.Linq.JToken>();
    var jsonEngine = new ScriptEngine<Newtonsoft.Json.Linq.JToken>(
        jsonOptions.CommandsProvider, jsonOptions.FunctionsProvider);

    var files = Directory.GetFiles(batchDir, "*.json").OrderBy(f => f).ToArray();

    foreach (var file in files)
    {
        var fileName = Path.GetFileName(file);
        try
        {
            var raw = File.ReadAllText(file);
            var doc = Newtonsoft.Json.Linq.JObject.Parse(raw);

            var inputNode = doc["input"];
            var scriptNode = doc["script"];
            if (inputNode == null || scriptNode == null)
            {
                WriteJsonLine(fileName, "ERR", error: "Missing input or script in fixture");
                continue;
            }

            var inputText = inputNode.ToString();
            var scriptText = scriptNode.ToString();

            // Reset context for each fixture
            var ctx = JsonExecutionContext.CreateDefault();
            var input = jsonAdapter.Parse(inputText);
            var result = jsonEngine.Execute(scriptText, input, ctx);

            if (result.Success)
            {
                var resultJson = jsonAdapter.Serialize(result.Data);
                WriteJsonLine(fileName, "OK", resultJson: resultJson);
            }
            else
            {
                var logEntries = ctx.GetLogEntries()
                    .Select(e => $"[{e.Level}] {e.Message}").ToArray();
                WriteJsonLine(fileName, "FAIL", error: "Transformation failed", log: logEntries);
            }
        }
        catch (Exception ex)
        {
            WriteJsonLine(fileName, "ERR", error: ex.Message);
        }
    }

    return 0;
}

static void WriteJsonLine(string file, string status, string? resultJson = null,
    string? error = null, string[]? log = null)
{
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);
    writer.WriteStartObject();
    writer.WriteString("file", file);
    writer.WriteString("status", status);
    if (resultJson != null)
        writer.WritePropertyName("result");
    if (resultJson != null)
        writer.WriteRawValue(resultJson);
    if (error != null)
        writer.WriteString("error", error);
    if (log != null)
    {
        writer.WriteStartArray("log");
        foreach (var l in log) writer.WriteStringValue(l);
        writer.WriteEndArray();
    }
    writer.WriteEndObject();
    writer.Flush();
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
}

// ── Single-file mode ────────────────────────────────────────────────────────

if (string.IsNullOrEmpty(inputPath) || string.IsNullOrEmpty(scriptPath))
{
    Console.Error.WriteLine("Error: --input and --script are required (or use --batch). Use --help for usage.");
    return 1;
}

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Error: Input file not found: {inputPath}");
    return 1;
}

if (!File.Exists(scriptPath))
{
    Console.Error.WriteLine($"Error: Script file not found: {scriptPath}");
    return 2;
}

// ── Format detection ─────────────────────────────────────────────────────────

var ext = Path.GetExtension(inputPath).ToLowerInvariant();
var format = ext switch
{
    ".json"          => "json",
    ".xml"           => "xml",
    ".yaml" or ".yml"=> "yaml",
    _                => null
};

if (format is null)
{
    Console.Error.WriteLine($"Error: Unknown file extension '{ext}'. Supported: .json, .xml, .yaml, .yml");
    return 3;
}

// ── Execute ───────────────────────────────────────────────────────────────────

try
{
    var inputText  = File.ReadAllText(inputPath);
    var scriptText = File.ReadAllText(scriptPath);

    // The extension is the author's own statement of the notation; the text is the fallback for
    // a script piped in under a name that says nothing.
    var notation = ScriptFormatDetector.FromFileExtension(Path.GetExtension(scriptPath))
                   ?? ScriptFormatDetector.Detect(scriptText);

    string output;

    try
    {
        output = RunTransform(format, inputText, scriptText, notation);
    }
    catch (FormatException ex)
    {
        Console.Error.WriteLine($"Error: Could not parse input: {ex.Message}");
        return 3;
    }
    catch (ScriptParseException ex)
    {
        Console.Error.WriteLine("Error: Could not parse script — it produced no commands.");
        foreach (var warning in ex.Warnings)
            Console.Error.WriteLine($"  {warning}");
        return 4;
    }
    catch (TransformFailedException ex)
    {
        Console.Error.WriteLine($"Error: Transformation failed.");
        foreach (var entry in ex.LogEntries)
            Console.Error.WriteLine($"  [{entry.Level}] {entry.Message}");
        return 5;
    }

    if (outputPath is not null)
        File.WriteAllText(outputPath, output);
    else
        Console.Write(output);

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    return 10;
}

// ── Transform dispatch ───────────────────────────────────────────────────────

static string RunTransform(string format, string inputText, string scriptText, ScriptFormat notation)
{
    // A script that crosses a format boundary cannot run on one engine — the node type changes
    // at the boundary — so it goes to the runner, which splits it and re-hosts each section.
    if (MultiFormatScriptRunner.CrossesAFormatBoundary(scriptText))
        return RunMultiFormat(format, inputText, scriptText);

    return format switch
    {
        "json" => Execute(JsonExecutionContext.CreateDefault(),        inputText, scriptText, notation),
        "xml"  => Execute(XmlExecutionContext.CreateWithNativeXPath(), inputText, scriptText, notation),
        "yaml" => Execute(YamlExecutionContext.CreateDefault(),        inputText, scriptText, notation),
        _      => throw new ArgumentException($"Unknown format: {format}")
    };
}

static TLio.FormatConverter.Core.FormatConverter CreateConverter()
{
    var converter = new TLio.FormatConverter.Core.FormatConverter();
    converter.Register(new JsonFormatAdapter());
    converter.Register(new XmlFormatAdapter());
    converter.Register(new YamlFormatAdapter());
    return converter;
}

/// <summary>The format id matching an execution context's node type.</summary>
static string FormatIdOf<TNode>(ExecutionContext<TNode> _) => typeof(TNode) switch
{
    var t when t == typeof(Newtonsoft.Json.Linq.JToken) => "json",
    var t when t == typeof(System.Xml.Linq.XElement) => "xml",
    _ => "yaml",
};

static string RunMultiFormat(string format, string inputText, string scriptText)
{
    var converter = CreateConverter();
    var runner = new MultiFormatScriptRunner(converter);
    runner.RegisterExecutor(SectionExecutor("json", converter,
        JsonExecutionContext.CreateDefault));
    runner.RegisterExecutor(SectionExecutor("xml", converter,
        XmlExecutionContext.CreateWithNativeXPath));
    runner.RegisterExecutor(SectionExecutor("yaml", converter,
        YamlExecutionContext.CreateDefault));

    var result = runner.Run(format, inputText, scriptText);

    if (!result.Success)
        throw new TransformFailedException(result.Logs);

    return result.Document;
}

static ScriptEngineSectionExecutor<TNode> SectionExecutor<TNode>(
    string formatId,
    TLio.FormatConverter.Core.FormatConverter converter,
    Func<ExecutionContext<TNode>> contextFactory)
{
    var options = ParseOptions<TNode>.CreateDefault();
    options.FunctionsProvider.RegisterMath<TNode>();
    options.FunctionsProvider.RegisterText<TNode>();
    options.FunctionsProvider.RegisterTimeDate<TNode>();
    options.CommandsProvider.RegisterETL<TNode>();
    options.CommandsProvider.RegisterFormatConversion<TNode>(converter, formatId);

    // A section is a slice of the original script, still in the notation it was written in — so
    // an XML script's sections arrive as XML. Without these the engine reads such a section as
    // an empty script and the section executor reports the notation it could not parse.
    var engine = new ScriptEngine<TNode>(options.CommandsProvider, options.FunctionsProvider)
        .UseXmlScripts()
        .UseYamlScripts();
    return new ScriptEngineSectionExecutor<TNode>(formatId, engine, () => contextFactory());
}

static string Execute<TNode>(
    ExecutionContext<TNode> context, string inputText, string scriptText, ScriptFormat notation)
{
    var adapter = context.NodeAdapter;

    TNode input;
    try { input = adapter.Parse(inputText); }
    catch (Exception ex) { throw new FormatException(ex.Message, ex); }

    var options = ParseOptions<TNode>.CreateDefault();
    options.FunctionsProvider.RegisterMath<TNode>();
    options.FunctionsProvider.RegisterText<TNode>();
    options.FunctionsProvider.RegisterTimeDate<TNode>();
    options.CommandsProvider.RegisterETL<TNode>();
    // convertValue works on the ordinary engine: it converts a value inside the document, not
    // the document itself, so the node type never changes.
    options.CommandsProvider.RegisterFormatConversion<TNode>(CreateConverter(), FormatIdOf(context));
    var engine = new ScriptEngine<TNode>(options.CommandsProvider, options.FunctionsProvider)
        .UseXmlScripts()
        .UseYamlScripts();

    var script = engine.Parse(scriptText, notation, adapter);

    // Nothing parsed *and* the parser said why: the text is broken, not deliberately empty.
    // An empty script is a legal script, so the warnings are what separate the two — without
    // them a typo would exit 0 having written the input straight back out.
    if (script.Count == 0 && script.ParseWarnings.Count > 0)
        throw new ScriptParseException(script.ParseWarnings);

    var result = engine.Execute(script, input, context);

    if (!result.Success)
        throw new TransformFailedException(context.GetLogEntries());

    return adapter.Serialize(result.Data);
}

// ── Helpers ──────────────────────────────────────────────────────────────────

internal sealed class TransformFailedException(
    IReadOnlyList<TLio.Core.Models.Logging.LogEntry> logEntries) : Exception
{
    public IReadOnlyList<TLio.Core.Models.Logging.LogEntry> LogEntries { get; } = logEntries;
}

/// <summary>
/// A script that parsed to nothing. Every parser answers unreadable text with an empty script
/// rather than an exception, so without this a typo in the script would exit 0 having written
/// the input back out unchanged.
/// </summary>
internal sealed class ScriptParseException(IReadOnlyList<string> warnings) : Exception
{
    public IReadOnlyList<string> Warnings { get; } = warnings;
}
