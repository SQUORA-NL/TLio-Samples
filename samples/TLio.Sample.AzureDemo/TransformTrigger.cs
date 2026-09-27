using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TLio.Sample.AzureDemo;

/// <summary>
/// POST /Transform — runs a TLio script against a JSON document.
///
/// Request body:  { "document": "&lt;json text&gt;", "commands": [ &lt;TLio command objects&gt; ], "iterations": &lt;int, optional&gt; }
/// Response body: { "success": bool, "result": &lt;transformed document&gt;, "log": [ "&lt;entry&gt;", ... ], "benchmark": {...}? }
///
/// "iterations" is optional and defaults to 1. When it's greater than 1, the script runs that
/// many times two ways — parsed fresh every time, and parsed once via
/// <see cref="TLio.Client.ScriptEngine{TNode}.Compile"/> and reused — and the response gains a
/// "benchmark" object with both timings, to make the cost of re-parsing visible on stage.
///
/// No auth, no queues, no database — this exists to be shown on a projector, not to run in
/// production.
///
/// Named "Trigger" rather than "Function" to keep it apart from TLio's own function concept
/// (<c>IFunction&lt;TNode&gt;</c>, <c>=datetime()</c> and friends) — this class is the Azure
/// Functions entry point, not a TLio function. The <c>[Function("Transform")]</c> attribute name
/// below is the Functions Worker SDK's own vocabulary and isn't ours to rename.
/// </summary>
public sealed class TransformTrigger
{
    private readonly TlioTransformer _transformer;
    private readonly ILogger<TransformTrigger> _logger;

    public TransformTrigger(TlioTransformer transformer, ILogger<TransformTrigger> logger)
    {
        _transformer = transformer;
        _logger = logger;
    }

    [Function("Transform")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "Transform")] HttpRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.Body))
            body = await reader.ReadToEndAsync();

        JObject request;
        try
        {
            request = JObject.Parse(body);
        }
        catch (JsonException ex)
        {
            return BadRequest($"Request body is not valid JSON: {ex.Message}");
        }

        var document = request["document"]?.Value<string>();
        var commands = request["commands"] as JArray;
        var iterations = Math.Clamp(request["iterations"]?.Value<int?>() ?? 1, 1, MaxIterations);

        if (string.IsNullOrWhiteSpace(document) || commands is null)
            return BadRequest("Request body must be { \"document\": <json text>, \"commands\": [ ... ] }.");

        var commandsJson = commands.ToString(Formatting.None);

        if (iterations > 1)
            return RunBenchmark(document, commandsJson, iterations);

        var outcome = _transformer.Transform(document, commandsJson);

        if (!outcome.Success)
            _logger.LogWarning("Transform script reported failures: {Log}", string.Join(" | ", outcome.Log));

        var response = new JObject
        {
            ["success"] = outcome.Success,
            ["result"] = outcome.Result,
            ["log"] = new JArray(outcome.Log)
        };

        return Json(response, StatusCodes.Status200OK);
    }

    // A stage demo's audience is patient, its Consumption-plan timeout budget is not.
    private const int MaxIterations = 20_000;

    private IActionResult RunBenchmark(string document, string commandsJson, int iterations)
    {
        var outcome = _transformer.Benchmark(document, commandsJson, iterations);

        if (!outcome.Success)
            _logger.LogWarning("Benchmark script reported failures: {Log}", string.Join(" | ", outcome.Log));

        var response = new JObject
        {
            ["success"] = outcome.Success,
            ["result"] = outcome.Result,
            ["log"] = new JArray(outcome.Log),
            ["benchmark"] = new JObject
            {
                ["iterations"] = outcome.Iterations,
                ["naiveMs"] = Math.Round(outcome.NaiveMs, 2),
                ["compileMs"] = Math.Round(outcome.CompileMs, 3),
                ["compiledExecuteMs"] = Math.Round(outcome.CompiledExecuteMs, 2),
                ["compiledTotalMs"] = Math.Round(outcome.CompiledTotalMs, 2),
                ["speedup"] = Math.Round(outcome.Speedup, 2)
            }
        };

        return Json(response, StatusCodes.Status200OK);
    }

    private static IActionResult BadRequest(string message) =>
        Json(new JObject { ["error"] = message }, StatusCodes.Status400BadRequest);

    private static ContentResult Json(JToken payload, int statusCode) => new()
    {
        Content = payload.ToString(Formatting.Indented),
        ContentType = "application/json",
        StatusCode = statusCode
    };
}
