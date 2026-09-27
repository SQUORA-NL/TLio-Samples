using TLio.FormatConverter;
using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using TLio.Client;
using TLio.Core.Models;
using TLio.Json;
using TLio.Xml;
using TLio.Yaml;
using TLio.Sample.Api;
using TLio.Sample.Api.Registry;
using YamlDotNet.RepresentationModel;

namespace TLio.Sample.Api.Services;

internal sealed class ScriptCompiler
{
    public ScriptRegistryEntry Compile(string slug, string scriptSource)
    {
        try
        {
            // One notation, compiled three times — once per document format the slug can later
            // be run against. Detecting it once rather than per engine keeps the three compiled
            // forms the same script; a script that reads as XML must not be read as JSON for the
            // JSON endpoint just because that engine was asked first.
            var notation = ScriptFormatDetector.Detect(scriptSource);

            // A script that crosses a format boundary has no single node type, so there is no
            // CompiledScript<TNode> to cache. Refusing at registration is better than compiling
            // three forms that would each run only part of it.
            if (MultiFormatScriptRunner.CrossesAFormatBoundary(scriptSource))
                throw new ScriptCompilationException(
                    "This script uses 'convert', which changes the document's format partway " +
                    "through. Such a script cannot be compiled per format and so cannot be " +
                    "registered under a slug — post it to /transform instead. To convert a value " +
                    "inside the document without changing the document's own format, use " +
                    "'convertValue', which compiles normally.");

            var jsonEngine = EngineSetup.CreateEngine<JToken>("json");
            var compiledJson = jsonEngine.Compile(scriptSource, notation, JsonExecutionContext.CreateDefault());

            var xmlEngine = EngineSetup.CreateEngine<XElement>("xml");
            var compiledXml = xmlEngine.Compile(scriptSource, notation, XmlExecutionContext.CreateWithNativeXPath());

            var yamlEngine = EngineSetup.CreateEngine<YamlNode>("yaml");
            var compiledYaml = yamlEngine.Compile(scriptSource, notation, YamlExecutionContext.CreateDefault());

            return new ScriptRegistryEntry(slug, scriptSource, compiledJson, compiledXml, compiledYaml, DateTimeOffset.UtcNow);
        }
        catch (ScriptCompilationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ScriptCompilationException(ex.Message);
        }
    }
}
