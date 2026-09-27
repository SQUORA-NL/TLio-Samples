using Newtonsoft.Json.Linq;
using TLio.Client;
using TLio.Extensions.ETL;
using TLio.Extensions.Math;
using TLio.Extensions.Text;
using TLio.FormatConverter;
using TLio.FormatConverter.Json;
using TLio.FormatConverter.Xml;
using TLio.FormatConverter.Yaml;
using TLio.Json;

namespace TLio.Sample.AfdApi;

/// <summary>
/// Builds the engine every conversion endpoint runs against.
///
/// afd1-to-afd2.tlio.json and afd1-to-afdshort.tlio.json open with a <c>convert</c> (AFD 1.0 XML
/// -&gt; JSON), so they cross a format boundary a plain <c>ScriptEngine</c> cannot run —
/// <see cref="MultiFormatScriptRunner"/> splits at the boundary and re-hosts each section on the
/// engine for its own format. afdshort-to-afd2.tlio.json never converts format, so it is just the
/// one-section case of the same runner — there is no reason to keep a second code path for it.
/// </summary>
internal static class EngineSetup
{
    public static TLio.FormatConverter.Core.FormatConverter Converter { get; } = CreateConverter();

    private static TLio.FormatConverter.Core.FormatConverter CreateConverter()
    {
        var converter = new TLio.FormatConverter.Core.FormatConverter();
        converter.Register(new JsonFormatAdapter());
        converter.Register(new XmlFormatAdapter());
        converter.Register(new YamlFormatAdapter());
        return converter;
    }

    private static ParseOptions<JToken> CreateJsonOptions()
    {
        var options = ParseOptions<JToken>.CreateDefault();
        options.FunctionsProvider.RegisterMath<JToken>();
        options.FunctionsProvider.RegisterText<JToken>();
        options.CommandsProvider.RegisterETL<JToken>();
        options.CommandsProvider.RegisterFormatConversion<JToken>(Converter, "json");
        return options;
    }

    /// <summary>
    /// One runner, shared by every endpoint: it dispatches on whether the script it is handed
    /// crosses a format boundary, so the same call site works for all three AFD directions.
    /// </summary>
    public static MultiFormatScriptRunner CreateRunner()
    {
        var runner = new MultiFormatScriptRunner(Converter);
        var jsonOptions = CreateJsonOptions();
        // Every AFD script is written in JSON notation (the paths inside it speak whichever
        // document format is current at that point) — no need to register XML/YAML script
        // notation parsing on top.
        var jsonEngine = new ScriptEngine<JToken>(jsonOptions.CommandsProvider, jsonOptions.FunctionsProvider);
        runner.RegisterExecutor(new ScriptEngineSectionExecutor<JToken>(
            "json", jsonEngine, () => JsonExecutionContext.CreateDefault()));
        return runner;
    }
}
