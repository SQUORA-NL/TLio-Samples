using System.Xml.Linq;
using TLio.FormatConverter.Json;
using TLio.FormatConverter;
using TLio.FormatConverter.Xml;
using TLio.FormatConverter.Yaml;
using Newtonsoft.Json.Linq;
using TLio.Client;
using TLio.Extensions.ETL;
using TLio.Extensions.Math;
using TLio.Extensions.Text;
using TLio.Extensions.TimeDate;
using TLio.Json;
using TLio.Xml;
using TLio.Yaml;
using YamlDotNet.RepresentationModel;

namespace TLio.Sample.Api;

/// <summary>
/// Builds the ParseOptions used by every endpoint in this sample.
///
/// The extension packs have to be registered here. A script that calls a function
/// from an unregistered pack fails as a whole, so leaving them out makes an
/// otherwise-valid script return no output at all rather than a partial result.
/// The CLI sample registers them; this one did not.
/// </summary>
internal static class EngineSetup
{
    /// <summary>
    /// The format adapters, shared by every engine and by the multi-format runner. Registration
    /// is explicit rather than automatic — some format libraries carry licence terms a host has
    /// to opt into.
    /// </summary>
    public static TLio.FormatConverter.Core.FormatConverter Converter { get; } = CreateConverter();

    private static TLio.FormatConverter.Core.FormatConverter CreateConverter()
    {
        var converter = new TLio.FormatConverter.Core.FormatConverter();
        converter.Register(new JsonFormatAdapter());
        converter.Register(new XmlFormatAdapter());
        converter.Register(new YamlFormatAdapter());
        return converter;
    }

    /// <param name="formatId">
    /// The format this engine's documents are in. <c>convertValue</c> needs it to decide whether a
    /// converted value comes back as structure or as text.
    /// </param>
    public static ParseOptions<TNode> CreateOptions<TNode>(string formatId)
    {
        var options = ParseOptions<TNode>.CreateDefault();
        options.FunctionsProvider.RegisterMath<TNode>();
        options.FunctionsProvider.RegisterText<TNode>();
        options.FunctionsProvider.RegisterTimeDate<TNode>();
        options.CommandsProvider.RegisterETL<TNode>();
        options.CommandsProvider.RegisterFormatConversion<TNode>(Converter, formatId);
        return options;
    }

    public static ScriptEngine<TNode> CreateEngine<TNode>(string formatId)
    {
        var options = CreateOptions<TNode>(formatId);
        return new ScriptEngine<TNode>(options.CommandsProvider, options.FunctionsProvider)
            // A registered script may be written in any of the three notations, whichever
            // format the document it later runs against is in.
            .UseXmlScripts()
            .UseYamlScripts();
    }

    /// <summary>
    /// A runner for scripts that change the document's format partway through. Those cannot run
    /// on one engine — the node type changes at the boundary — so the runner splits the script
    /// and hands each section to the engine for its format.
    /// </summary>
    public static MultiFormatScriptRunner CreateRunner()
    {
        var runner = new MultiFormatScriptRunner(Converter);
        runner.RegisterExecutor(new ScriptEngineSectionExecutor<JToken>(
            "json", CreateEngine<JToken>("json"), () => JsonExecutionContext.CreateDefault()));
        runner.RegisterExecutor(new ScriptEngineSectionExecutor<XElement>(
            "xml", CreateEngine<XElement>("xml"), () => XmlExecutionContext.CreateWithNativeXPath()));
        runner.RegisterExecutor(new ScriptEngineSectionExecutor<YamlNode>(
            "yaml", CreateEngine<YamlNode>("yaml"), () => YamlExecutionContext.CreateDefault()));
        return runner;
    }
}
