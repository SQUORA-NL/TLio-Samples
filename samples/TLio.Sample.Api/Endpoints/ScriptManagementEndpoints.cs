using System.Text.RegularExpressions;
using TLio.Sample.Api.Registry;
using TLio.Sample.Api.Services;

namespace TLio.Sample.Api.Endpoints;

internal static class ScriptManagementEndpoints
{
    private static readonly Regex SlugPattern = new(@"^[a-z0-9\-_]+$", RegexOptions.Compiled);

    public static void Map(WebApplication app)
    {
        // POST /scripts — register or replace a script
        app.MapPost("/scripts", async (
            HttpRequest request,
            IScriptRegistry registry,
            ScriptCompiler compiler,
            RegistrationPayloadParser parser,
            ILogger<Program> logger) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();

            var parsed = parser.Parse(body, request.ContentType);
            if (parsed is null)
            {
                logger.LogWarning("Cannot parse registration payload, Content-Type: {CT}", request.ContentType);
                return Results.BadRequest(new { error = "Cannot parse registration payload" });
            }

            var (slug, script) = parsed.Value;

            if (!SlugPattern.IsMatch(slug))
                return Results.BadRequest(new { error = $"Invalid slug format: '{slug}'" });

            ScriptRegistryEntry compiled;
            try { compiled = compiler.Compile(slug, script); }
            catch (ScriptCompilationException ex)
            {
                logger.LogWarning("Compilation failed for slug '{Slug}': {Error}", slug, ex.Message);
                return Results.BadRequest(new { error = ex.Message });
            }

            var isReplace = registry.TryGet(slug, out _);
            registry.Add(compiled);
            logger.LogInformation("Slug '{Slug}' {Action} at {Time}", slug, isReplace ? "replaced" : "registered", compiled.RegisteredAt);

            return isReplace
                ? Results.Ok(new { slug, status = "replaced" })
                : Results.Created($"/scripts/{slug}", new { slug, status = "registered" });
        });

        // GET /scripts — list all registered slugs
        app.MapGet("/scripts", (IScriptRegistry registry) =>
        {
            var list = registry.List().Select(e => new
            {
                slug = e.Slug,
                source = e.Source,
                registeredAt = e.RegisteredAt
            });
            return Results.Ok(list);
        });

        // DELETE /scripts/{slug} — remove a slug
        app.MapDelete("/scripts/{slug}", (
            string slug,
            IScriptRegistry registry,
            ILogger<Program> logger) =>
        {
            if (!SlugPattern.IsMatch(slug))
                return Results.BadRequest(new { error = $"Invalid slug format: '{slug}'" });

            if (registry.Delete(slug))
            {
                logger.LogInformation("Slug '{Slug}' deleted", slug);
                return Results.NoContent();
            }

            logger.LogWarning("DELETE: slug '{Slug}' not found", slug);
            return Results.NotFound(new { error = $"Slug '{slug}' is not registered" });
        });
    }
}
