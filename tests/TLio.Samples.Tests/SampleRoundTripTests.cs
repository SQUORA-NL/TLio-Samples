using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TLio.Client;
using TLio.Core.Contracts;
using TLio.Extensions.ETL;
using TLio.Extensions.Looping;
using TLio.Extensions.Math;
using TLio.Extensions.Text;
using TLio.Extensions.TimeDate;
using TLio.Json;
using TLio.Xml;
using TLio.Yaml;
using YamlDotNet.RepresentationModel;

namespace TLio.Samples.Tests;

/// <summary>
/// Every sample in <c>docs/samples</c>, run against the TLio packages this repository restores,
/// has to produce the output committed next to it.
/// </summary>
/// <remarks>
/// <para>
/// The samples are the largest scripts people copy: decision tables, resolves, nested settings,
/// all three notations. Each is parsed, written back out as JSON with
/// <see cref="TLioConvert.Serialize{TNode}"/>, parsed again and run — so a sample that stops
/// producing its committed output, or whose settings no longer survive serialization, fails here.
/// </para>
/// <para>
/// This test used to live in TLio.Parity.Tests in the TLio repository and moved here with the
/// samples. Samples that cross a format boundary are skipped: <c>convert</c> needs
/// <c>MultiFormatScriptRunner</c> from TLio.FormatConverter, whose own tests cover them.
/// </para>
/// </remarks>
[TestFixture]
public class SampleRoundTripTests
{
    [TestCaseSource(nameof(SampleDirectories))]
    public void ASampleSurvivesParseSerializeParse(string directory)
    {
        var scriptPath = Directory.EnumerateFiles(directory, "script.*").Single();
        var inputPath  = Directory.EnumerateFiles(directory, "input.*").Single();
        var outputPath = Directory.EnumerateFiles(directory, "output.*").Single();

        var (actual, expected, success, log) = Path.GetExtension(inputPath) switch
        {
            ".xml"  => RunXmlSample(scriptPath, inputPath, outputPath),
            ".yaml" => RunYamlSample(scriptPath, inputPath, outputPath),
            _       => RunJsonSample(scriptPath, inputPath, outputPath),
        };

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True, $"the round-tripped sample did not run: {log}");
            Assert.That(actual, Is.EqualTo(expected));
        });
    }

    private static IEnumerable<TestCaseData> SampleDirectories()
    {
        var samples = Path.Combine(RepositoryRoot, "docs", "samples");

        foreach (var directory in Directory.EnumerateDirectories(samples, "*", SearchOption.AllDirectories).Order())
        {
            if (!Directory.EnumerateFiles(directory, "script.*").Any()) continue;
            if (!Directory.EnumerateFiles(directory, "input.*").Any()) continue;
            if (!Directory.EnumerateFiles(directory, "output.*").Any()) continue;

            var script = File.ReadAllText(Directory.EnumerateFiles(directory, "script.*").Single());
            if (CrossesAFormatBoundary(script)) continue;

            yield return new TestCaseData(directory)
                .SetName($"ASampleSurvivesParseSerializeParse({Path.GetRelativePath(samples, directory)})");
        }
    }

    private static bool CrossesAFormatBoundary(string script) =>
        script.Contains("\"command\": \"convert\"") ||
        script.Contains("command: convert") ||
        script.Contains("<convert ");

    private static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "samples")))
                dir = dir.Parent;

            return dir?.FullName ?? throw new DirectoryNotFoundException("docs/samples is not above the test directory.");
        }
    }

    /// <summary>
    /// The same command and function set for every format — every optional pack included, as
    /// the CLI sample registers them.
    /// </summary>
    private static ParseOptions<TNode> Options<TNode>()
    {
        var options = ParseOptions<TNode>.CreateDefault();
        options.FunctionsProvider.RegisterText<TNode>();
        options.FunctionsProvider.RegisterMath<TNode>();
        options.FunctionsProvider.RegisterTimeDate<TNode>();
        options.CommandsProvider.RegisterETL<TNode>();
        options.CommandsProvider.RegisterLooping<TNode>();
        return options;
    }

    private static (string Actual, string Expected, bool Success, string Log) RunJsonSample(
        string scriptPath, string inputPath, string outputPath)
    {
        var options = Options<JToken>();
        var engine  = new ScriptEngine<JToken>(options.CommandsProvider, options.FunctionsProvider);
        var adapter = JsonExecutionContext.CreateDefault().NodeAdapter;
        var context = JsonExecutionContext.CreateDefault();

        var json   = TLioConvert.Serialize(engine.Parse(File.ReadAllText(scriptPath), adapter), adapter);
        var result = engine.Execute(json, JToken.Parse(File.ReadAllText(inputPath)), context);

        return (result.Data.ToString(),
                JToken.Parse(File.ReadAllText(outputPath)).ToString(),
                result.Success,
                Log(context));
    }

    private static (string Actual, string Expected, bool Success, string Log) RunXmlSample(
        string scriptPath, string inputPath, string outputPath)
    {
        var options = Options<XElement>();
        var adapter = new XmlNodeAdapter();
        var parser  = new XmlScriptParser<XElement>(options.CommandsProvider, options.FunctionsProvider, adapter);
        var engine  = new ScriptEngine<XElement>(options.CommandsProvider, options.FunctionsProvider);
        var context = XmlExecutionContext.CreateWithNativeXPath();

        var json   = TLioConvert.Serialize(parser.ParseScript(File.ReadAllText(scriptPath)), adapter);
        var result = engine.Parse(json, adapter).Execute(adapter.Parse(File.ReadAllText(inputPath)), context);

        return (result.Data.ToString(),
                XDocument.Parse(File.ReadAllText(outputPath)).Root!.ToString(),
                result.Success,
                Log(context));
    }

    private static (string Actual, string Expected, bool Success, string Log) RunYamlSample(
        string scriptPath, string inputPath, string outputPath)
    {
        var options = Options<YamlNode>();
        var context = YamlExecutionContext.CreateDefault();
        var adapter = context.NodeAdapter;
        var parser  = new YamlScriptParser<YamlNode>(options.CommandsProvider, options.FunctionsProvider, adapter);
        var engine  = new ScriptEngine<YamlNode>(options.CommandsProvider, options.FunctionsProvider);

        var json   = TLioConvert.Serialize(parser.ParseScript(File.ReadAllText(scriptPath)), adapter);
        var result = engine.Parse(json, adapter).Execute(adapter.Parse(File.ReadAllText(inputPath)), context);

        return (adapter.Serialize(result.Data, false).Trim(),
                File.ReadAllText(outputPath).Trim(),
                result.Success,
                Log(context));
    }

    private static string Log<TNode>(IExecutionContext<TNode> context) =>
        string.Join("; ", context.GetLogEntries().Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message));
}
