using Newtonsoft.Json.Linq;
using TLio.Client;
using TLio.Json;
using TLio.Sample.Actus.Api;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var scriptsDir = Path.Combine(AppContext.BaseDirectory, "Scripts");
var samplesDir = Path.Combine(AppContext.BaseDirectory, "SampleInput");

var engine = EngineSetup.CreateEngine();
var adapter = JsonExecutionContext.CreateDefault().NodeAdapter;

// Each script is parsed once, here, rather than on every request.
var simple = engine.Compile(File.ReadAllText(Path.Combine(scriptsDir, "pam-simple.json")), adapter);
var envelope = engine.Compile(File.ReadAllText(Path.Combine(scriptsDir, "pam-envelope.json")), adapter);

app.MapGet("/", () => Results.Ok(new
{
    service = "TLio ACTUS PAM (Principal at Maturity) demo",
    endpoints = new[]
    {
        new { path = "/actus/pam", body = "bare PAM contract terms", result = "{ events, summary }" },
        new { path = "/actus/pam/envelope", body = "{ contract, scenario }", result = "{ events, summary }" },
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
    return Results.Text(File.ReadAllText(path), "application/json");
});

app.MapPost("/actus/pam", async (HttpRequest request) =>
    RunScript(simple, await ReadBody(request)));

app.MapPost("/actus/pam/envelope", async (HttpRequest request) =>
    RunScript(envelope, await ReadBody(request)));

app.Run();
return;

static async Task<string> ReadBody(HttpRequest request)
{
    using var reader = new StreamReader(request.Body);
    return await reader.ReadToEndAsync();
}

IResult RunScript(CompiledScript<JToken> script, string inputDocument)
{
    if (string.IsNullOrWhiteSpace(inputDocument))
        return Results.BadRequest(new { error = "Request body is empty." });

    JToken input;
    try { input = JToken.Parse(inputDocument); }
    catch (Exception ex) { return Results.BadRequest(new { error = $"Request body is not valid JSON: {ex.Message}" }); }

    var context = JsonExecutionContext.CreateDefault();
    var result = script.Execute(input, context);
    if (!result.Success)
    {
        var warnings = context.GetLogEntries().Select(e => $"{e.Level}: {e.Message}").ToList();
        return Results.Content(
            new JObject { ["document"] = result.Data, ["warnings"] = JArray.FromObject(warnings) }
                .ToString(Newtonsoft.Json.Formatting.None),
            "application/json", statusCode: 422);
    }
    var content = result.Data.ToString(Newtonsoft.Json.Formatting.None);
    return Results.Content(content, "application/json");
}
