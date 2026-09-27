using System.Text.Json;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TLio.Client;
using TLio.Core.Contracts;
using TLio.Json;
using TLio.Xml;
using TLio.Yaml;
using YamlDotNet.RepresentationModel;

namespace TLio.Sample.DockerPlugin.Endpoints;

/// <summary>
/// POST /transform/{format} — run a script supplied in the request against a document supplied
/// in the same request. The ad-hoc counterpart to <c>/run/{slug}</c>, which executes a script
/// registered earlier.
/// </summary>
internal static class TransformEndpoints
{
    private static readonly JsonSerializerOptions PayloadOptions =
        new() { PropertyNameCaseInsensitive = true };

    public static void Map(WebApplication app)
    {
        app.MapPost("/transform/{format}", async (
            string format,
            HttpRequest request,
            MutableFunctionsProvider<JToken> functionsProvider,
            ILogger<Program> log) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "Request body must not be empty." });

            TransformRequest? payload;
            try { payload = JsonSerializer.Deserialize<TransformRequest>(body, PayloadOptions); }
            catch (Exception ex) { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }

            if (payload?.Input is null || payload.Script is null)
                return Results.BadRequest(new { error = "Fields 'input' and 'script' are required." });

            var script = AsText(payload.Script.Value);

            return format.ToLowerInvariant() switch
            {
                "json" => Run(
                    JsonExecutionContext.CreateDefault(),
                    // The JSON engine gets the mutable provider, so functions from hot-loaded
                    // plugin packs are visible here. XML and YAML get built-ins only: plugins
                    // register as FunctionsProvider<JToken> (see PluginLoader), and there is no
                    // XElement or YamlNode equivalent to hand them.
                    functionsProvider,
                    AsText(payload.Input.Value), script, log),

                "xml" => XmlInput(payload.Input.Value) is { } xml
                    ? Run(XmlExecutionContext.CreateWithNativeXPath(),
                          ParseOptions<XElement>.CreateDefault().FunctionsProvider,
                          xml, script, log)
                    : Results.BadRequest(new { error = "For format 'xml', 'input' must be a string holding the XML document." }),

                // JSON is a subset of YAML, so a JSON object is accepted here as well as a
                // string holding YAML text.
                "yaml" => Run(
                    YamlExecutionContext.CreateDefault(),
                    ParseOptions<YamlNode>.CreateDefault().FunctionsProvider,
                    AsText(payload.Input.Value), script, log),

                _ => Results.BadRequest(new
                {
                    error = $"Unsupported format '{format}'. Supported: json, xml, yaml."
                })
            };
        });
    }

    /// <summary>
    /// Runs one script against one document in whichever format the context speaks. Every
    /// format-specific decision — how the text is parsed, which path language the script's
    /// paths are in, how the result is serialised — lives behind the context and the adapter,
    /// which is the whole reason this method can be generic.
    /// </summary>
    private static IResult Run<TNode>(
        IExecutionContext<TNode> context,
        IFunctionsProvider<TNode> functions,
        string documentText,
        string script,
        ILogger log)
    {
        TNode input;
        try { input = context.NodeAdapter.Parse(documentText); }
        catch (Exception ex) { return Results.BadRequest(new { error = $"Invalid input document: {ex.Message}" }); }

        var engine = new ScriptEngine<TNode>(ParseOptions<TNode>.CreateDefault().CommandsProvider, functions)
            .UseXmlScripts()
            .UseYamlScripts();

        TLio.Core.Models.TLioExecutionResult<TNode> result;
        try { result = engine.Execute(script, input, context); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Script execution threw an exception.");
            return Results.Ok(new { success = false, error = ex.Message });
        }

        if (!result.Success)
        {
            var errors = string.Join("; ", context.GetLogEntries().Select(e => e.Message));
            return Results.Ok(new { success = false, error = errors });
        }

        var serialised = context.NodeAdapter.Serialize(result.Data);

        // JSON comes back as a JSON value so the response stays a document rather than a string
        // holding one; XML and YAML have no JSON form and come back as text.
        if (typeof(TNode) == typeof(JToken))
        {
            using var parsed = JsonDocument.Parse(serialised);
            return Results.Ok(new { success = true, data = parsed.RootElement.Clone() });
        }

        return Results.Ok(new { success = true, data = serialised });
    }

    /// <summary>
    /// A JSON string is the text itself (a script in XML or YAML notation, or an XML or YAML
    /// document); any other JSON value is that value re-serialised.
    /// </summary>
    private static string AsText(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();

    private static string? XmlInput(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    /// <param name="Input">
    /// The document. A JSON value for <c>json</c>; a string holding the document text for
    /// <c>xml</c>; either for <c>yaml</c>.
    /// </param>
    /// <param name="Script">
    /// The script, as a JSON array or as a string in JSON, XML or YAML script notation. Its
    /// paths must speak the document format's path language — XPath for <c>xml</c>.
    /// </param>
    private sealed record TransformRequest(
        JsonElement? Input,
        JsonElement? Script);
}
