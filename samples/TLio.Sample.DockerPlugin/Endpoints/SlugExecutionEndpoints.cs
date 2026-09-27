using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using TLio.Json;
using TLio.Xml;
using TLio.Yaml;
using TLio.Sample.DockerPlugin.Registry;
using TLio.Sample.DockerPlugin.Services;
using YamlDotNet.RepresentationModel;

namespace TLio.Sample.DockerPlugin.Endpoints;

internal static class SlugExecutionEndpoints
{
    private static readonly Regex SlugPattern = new(@"^[a-z0-9\-_]+$", RegexOptions.Compiled);

    public static void Map(WebApplication app)
    {
        app.MapPost("/run/{slug}", async (
            string slug,
            HttpRequest request,
            IScriptRegistry registry,
            FormatDetector formatDetector,
            ILogger<Program> logger) =>
        {
            if (!SlugPattern.IsMatch(slug))
                return Results.BadRequest(new { error = $"Invalid slug format: '{slug}'" });

            if (!registry.TryGet(slug, out var entry))
            {
                logger.LogWarning("Slug '{Slug}' not found in registry", slug);
                return Results.NotFound(new { error = $"Slug '{slug}' is not registered" });
            }

            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();

            var format = formatDetector.DetectFormat(request.ContentType, body);
            if (format is null)
            {
                logger.LogWarning("Cannot detect input format for slug '{Slug}', Content-Type: {CT}", slug, request.ContentType);
                return Results.BadRequest(new { error = "Cannot detect input format" });
            }

            return format switch
            {
                "json" => ExecuteJson(body, entry, slug, logger),
                "xml"  => ExecuteXml(body, entry, slug, logger),
                "yaml" => ExecuteYaml(body, entry, slug, logger),
                _      => Results.BadRequest(new { error = $"Unsupported format: {format}" })
            };
        });
    }

    private static IResult ExecuteJson(string body, ScriptRegistryEntry entry, string slug, ILogger logger)
    {
        var context = JsonExecutionContext.CreateDefault();
        JToken input;
        try { input = context.NodeAdapter.Parse(body); }
        catch (Exception ex) { return Results.BadRequest(new { error = $"Invalid JSON body: {ex.Message}" }); }

        var result = entry.CompiledJson.Execute(input, context);
        if (!result.Success)
        {
            var errors = string.Join("; ", context.GetLogEntries().Select(e => e.Message));
            logger.LogError("Execution failed for slug '{Slug}': {Errors}", slug, errors);
            return Results.UnprocessableEntity(new { error = errors });
        }

        return Results.Content(context.NodeAdapter.Serialize(result.Data), "application/json");
    }

    private static IResult ExecuteXml(string body, ScriptRegistryEntry entry, string slug, ILogger logger)
    {
        var context = XmlExecutionContext.CreateWithNativeXPath();
        XElement input;
        try { input = context.NodeAdapter.Parse(body); }
        catch (Exception ex) { return Results.BadRequest(new { error = $"Invalid XML body: {ex.Message}" }); }

        var result = entry.CompiledXml.Execute(input, context);
        if (!result.Success)
        {
            var errors = string.Join("; ", context.GetLogEntries().Select(e => e.Message));
            logger.LogError("Execution failed for slug '{Slug}': {Errors}", slug, errors);
            return Results.UnprocessableEntity(new { error = errors });
        }

        return Results.Content(context.NodeAdapter.Serialize(result.Data), "application/xml");
    }

    private static IResult ExecuteYaml(string body, ScriptRegistryEntry entry, string slug, ILogger logger)
    {
        var context = YamlExecutionContext.CreateDefault();
        YamlNode input;
        try { input = context.NodeAdapter.Parse(body); }
        catch (Exception ex) { return Results.BadRequest(new { error = $"Invalid YAML body: {ex.Message}" }); }

        var result = entry.CompiledYaml.Execute(input, context);
        if (!result.Success)
        {
            var errors = string.Join("; ", context.GetLogEntries().Select(e => e.Message));
            logger.LogError("Execution failed for slug '{Slug}': {Errors}", slug, errors);
            return Results.UnprocessableEntity(new { error = errors });
        }

        return Results.Content(context.NodeAdapter.Serialize(result.Data), "application/yaml");
    }
}
