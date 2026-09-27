using TLio.Sample.Api.Endpoints;
using TLio.Sample.Api.Registry;
using TLio.Sample.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
    o.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long?>("SlugCache:MaxBodySizeBytes") ?? 10 * 1024 * 1024);

// ── Slug-cache services ───────────────────────────────────────────────────────

builder.Services.AddSingleton<IScriptRegistry, ScriptRegistry>();
builder.Services.AddSingleton<ScriptCompiler>();
builder.Services.AddSingleton<FormatDetector>();
builder.Services.AddSingleton<RegistrationPayloadParser>();
builder.Services.AddSingleton<StartupScriptLoader>();

var app = builder.Build();

app.Urls.Add("http://localhost:5100");

// ── Slug-cache endpoints ──────────────────────────────────────────────────────

SlugExecutionEndpoints.Map(app);
ScriptManagementEndpoints.Map(app);

// ── Legacy transform endpoints ────────────────────────────────────────────────

static async Task<IResult> HandleTransform(string format, string contentType, HttpRequest request)
{
    using var reader = new StreamReader(request.Body);
    var payload = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(payload))
        return Results.BadRequest(new { error = "Request body must not be empty." });

    var result = TLio.Sample.Api.TransformService.Execute(format, payload);

    if (result is null)
        return Results.StatusCode(415);

    var (success, output, log) = result.Value;

    if (!success)
        return Results.UnprocessableEntity(new
        {
            error = "Transformation failed.",
            log   = log.Select(e => $"[{e.Level}] {e.Message}").ToArray()
        });

    return Results.Content(output, contentType);
}

app.MapPost("/transform/json", (HttpRequest req) =>
    HandleTransform("json", "application/json", req));

app.MapPost("/transform/xml", (HttpRequest req) =>
    HandleTransform("xml", "application/xml", req));

app.MapPost("/transform/yaml", (HttpRequest req) =>
    HandleTransform("yaml", "text/yaml", req));

// ── Startup: seed registry from config ───────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var loader  = scope.ServiceProvider.GetRequiredService<StartupScriptLoader>();
    var reg     = scope.ServiceProvider.GetRequiredService<IScriptRegistry>();
    var comp    = scope.ServiceProvider.GetRequiredService<ScriptCompiler>();
    var logger  = scope.ServiceProvider.GetRequiredService<ILogger<StartupScriptLoader>>();
    var config  = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    await loader.LoadAsync(reg, comp, logger, config);
}

app.Run();

public partial class Program { }
