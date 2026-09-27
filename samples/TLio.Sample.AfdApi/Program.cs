using TLio.Sample.AfdApi;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var scriptsDir = Path.Combine(AppContext.BaseDirectory, "Scripts");
var samplesDir = Path.Combine(AppContext.BaseDirectory, "SampleInput");
var runner = EngineSetup.CreateRunner();

// Each script is parsed once, here, rather than on every request — see ConversionRegistry.
var conversions = ConversionRegistry.Create(scriptsDir, runner);

// Run each direction once before accepting traffic, so the first real request doesn't pay the
// JIT tiering cost that parsing alone no longer hides.
conversions.WarmUp("afd1-to-afd2", File.ReadAllText(Path.Combine(samplesDir, "afd1-clean-sample.xml")));
conversions.WarmUp("afd1-to-afdshort", File.ReadAllText(Path.Combine(samplesDir, "afd1-clean-sample.xml")));
conversions.WarmUp("afdshort-to-afd2", File.ReadAllText(Path.Combine(samplesDir, "afdshort-clean-sample.json")));

app.MapGet("/", () => Results.Ok(new
{
    service = "TLio AFD conversion demo",
    directions = new[]
    {
        new { path = "/convert/afd1-to-afd2", from = "AFD 1.0 (XML)", to = "AFD 2.0 (JSON)" },
        new { path = "/convert/afd1-to-afdshort", from = "AFD 1.0 (XML)", to = "AFD Short (JSON)" },
        new { path = "/convert/afdshort-to-afd2", from = "AFD Short (JSON)", to = "AFD 2.0 (JSON)" },
    },
    samples = Directory.Exists(samplesDir)
        ? Directory.GetFiles(samplesDir).Select(Path.GetFileName)
        : Array.Empty<string?>(),
}));

app.MapGet("/samples/{name}", (string name) =>
{
    var path = Path.Combine(samplesDir, name);
    if (!File.Exists(path))
        return Results.NotFound(new { error = $"No sample named '{name}'. See GET / for the list." });
    var contentType = name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? "application/xml" : "application/json";
    return Results.Text(File.ReadAllText(path), contentType);
});

// AFD 1.0 (XML) -> AFD 2.0 (JSON): restructure + rename, one pass, one convert boundary.
app.MapPost("/convert/afd1-to-afd2", async (HttpRequest request) =>
    RunConversion("afd1-to-afd2", await ReadBody(request)));

// AFD 1.0 (XML) -> AFD Short (JSON): restructure only, AFD 1.0 labels kept.
app.MapPost("/convert/afd1-to-afdshort", async (HttpRequest request) =>
    RunConversion("afd1-to-afdshort", await ReadBody(request)));

// AFD Short (JSON) -> AFD 2.0 (JSON): rename-only, no format boundary.
app.MapPost("/convert/afdshort-to-afd2", async (HttpRequest request) =>
    RunConversion("afdshort-to-afd2", await ReadBody(request)));

// The in-process warm-up above JITs the TLio engine's own code paths, but not Kestrel's request
// pipeline, routing, or JSON response writing — those only JIT on their first real HTTP call.
// Self-pinging each endpoint once real traffic can reach it moves that cost here too, off the
// first client to hit each direction.
app.Lifetime.ApplicationStarted.Register(() => _ = WarmUpHttpPipelineAsync(app, samplesDir));

app.Run();
return;

static async Task WarmUpHttpPipelineAsync(WebApplication app, string samplesDir)
{
    var baseUrl = app.Urls.FirstOrDefault();
    if (baseUrl is null) return;

    using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
    await PingOnce(client, "/convert/afd1-to-afd2", "application/xml", Path.Combine(samplesDir, "afd1-clean-sample.xml"));
    await PingOnce(client, "/convert/afd1-to-afdshort", "application/xml", Path.Combine(samplesDir, "afd1-clean-sample.xml"));
    await PingOnce(client, "/convert/afdshort-to-afd2", "application/json", Path.Combine(samplesDir, "afdshort-clean-sample.json"));
}

static async Task PingOnce(HttpClient client, string path, string contentType, string sampleFile)
{
    using var content = new StringContent(await File.ReadAllTextAsync(sampleFile));
    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
    using var response = await client.PostAsync(path, content);
    response.EnsureSuccessStatusCode();
}

static async Task<string> ReadBody(HttpRequest request)
{
    using var reader = new StreamReader(request.Body);
    return await reader.ReadToEndAsync();
}

IResult RunConversion(string direction, string inputDocument)
{
    if (string.IsNullOrWhiteSpace(inputDocument))
        return Results.BadRequest(new { error = "Request body is empty." });

    // A conversion never aborts on data — an unmapped attribute, an unknown entity, a value that
    // fails a type conversion are all collected as issues, not thrown. Only a malformed script or
    // an unreadable input fails here, and then the input is returned unchanged with success=false
    // — that is the engine's contract, not this endpoint's.
    var result = conversions.Run(direction, inputDocument);
    return Results.Content(result.Document, "application/json", statusCode: result.Success ? 200 : 422);
}
