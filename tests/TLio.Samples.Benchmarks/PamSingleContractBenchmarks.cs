using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Newtonsoft.Json.Linq;
using TLio.Client;
using TLio.Extensions.Looping;
using TLio.Extensions.Math;
using TLio.Extensions.Text;
using TLio.Extensions.TimeDate;
using TLio.Json;

namespace TLio.Samples.Benchmarks;

/// <summary>
/// Latency of ONE PAM contract through the sample's <c>pam-simple.json</c>, warmed up, on one
/// thread: the "one quote on request" row. The script is compiled once in setup (as the sample's
/// host does per endpoint); each invocation builds a fresh execution context, executes, and
/// returns the event count. Two shapes: the 41-event 10-year quarterly loan used by the
/// throughput benchmark, and ACTUS-I's 50-year monthly workload (601 events here).
///
///   dotnet run -c Release --project tests/TLio.Samples.Benchmarks -- --filter "*"
/// </summary>
[MemoryDiagnoser]
public class PamSingleContractBenchmarks
{
    private CompiledScript<JToken> _script = null!;
    private CompiledScript<JToken> _reference = null!;
    private CompiledScript<JToken> _lifeScript = null!;
    private JObject[] _life = null!;
    private int _lifeIndex;
    private JToken _quarterly10y = null!;
    private JToken _monthly50y = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = ParseOptions<JToken>.CreateDefault();
        options.FunctionsProvider.RegisterMath<JToken>();
        options.FunctionsProvider.RegisterText<JToken>();
        options.FunctionsProvider.RegisterTimeDate<JToken>();
        options.CommandsProvider.RegisterLooping<JToken>();
        var engine = new ScriptEngine<JToken>(options.CommandsProvider, options.FunctionsProvider);
        var adapter = JsonExecutionContext.CreateDefault().NodeAdapter;
        _script = engine.Compile(File.ReadAllText(ScriptPath("pam-simple.json")), adapter);
        _reference = engine.Compile(File.ReadAllText(ScriptPath("pam-reference.json")), adapter);
        _lifeScript = engine.Compile(File.ReadAllText(ScriptPath("life-project.json")), adapter);
        _life = LifeContracts();

        _quarterly10y = Contract("PAM0000001", "RPA", 10_000, 0.01, "A360", "2020-01-01", "2030-01-01", 3);
        _monthly50y = Contract("ULT50Y", "RPA", 100_000, 0.05, "A365", "2025-01-01", "2075-01-01", 1);

        // A benchmark of a script that quietly produced the wrong schedule measures nothing.
        Expect(Simple_41_Events(), 41);
        Expect(Simple_601_Events(), 601);
        Expect(Reference_42_Events(), 42);
        Expect(Reference_602_Events(), 602);
        Expect(Life_30_Steps(), 30);
        Expect(Life_600_Steps(), 600);
    }

    // Life: one policy, warmed. The 42-contract cycle is ACTUS-I's LifeInsuranceBenchmarkData.
    // 30 annual steps is the horizon of ACTUS-I's published 100,000-policy row; 600 monthly steps
    // is its "Ultimate" 50-year workload.
    [Benchmark(Description = "life-project: 1 policy, 30 annual steps")]
    public int Life_30_Steps() => RunLife(_life[_lifeIndex++ % 42], 30, 1.0);

    [Benchmark(Description = "life-project: 1 policy, 600 monthly steps")]
    public int Life_600_Steps() => RunLife(_life[_lifeIndex++ % 42], 600, 1.0 / 12.0);

    private int RunLife(JObject contract, int steps, double dt)
    {
        var input = new JObject { ["contract"] = contract.DeepClone(), ["timeSteps"] = steps, ["dtYears"] = dt };
        var context = JsonExecutionContext.CreateDefault();
        var result = _lifeScript.Execute(input, context);
        if (!result.Success)
            throw new InvalidOperationException("Life projection failed to execute.");
        return result.Data.SelectToken("$.steps")!.Count();
    }

    /// <summary>ACTUS-I's LifeInsuranceBenchmarkData, as the input documents life-project.json reads.</summary>
    public static JObject[] LifeContracts()
    {
        var list = new List<JObject>(42);
        var ages = new[] { 25.0, 35, 40, 45, 50, 55, 60, 65 };
        var index = 0;
        foreach (var age in ages)
            for (var gender = 0; gender < 3; gender++)
                foreach (var smoker in new[] { 0, 1 })
                {
                    if (++index > 42) break;
                    var sumAssured = (index % 4) switch { 0 => 50_000.0, 1 => 100_000.0, 2 => 200_000.0, _ => 500_000.0 };
                    list.Add(new JObject
                    {
                        ["currentState"] = 1,
                        ["smokerStatus"] = smoker,
                        ["insuredGender"] = gender,
                        ["premiumMode"] = index % 3,
                        ["ageAtEval"] = age,
                        ["sumAssured"] = sumAssured,
                        ["premiumAmount"] = sumAssured * 0.001,
                        ["yearsInForce"] = (index % 4) switch { 0 => 0.5, 1 => 2.0, 2 => 5.0, _ => 10.0 },
                        ["extraPremBps"] = (index % 4) switch { 0 => 0, 1 => 100, 2 => 200, _ => 500 },
                    });
                }
        return list.ToArray();
    }

    private static void Expect(int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Expected {expected} events, got {actual}.");
    }

    [Benchmark(Description = "pam-simple: 1 contract, 10y quarterly (41 events)")]
    public int Simple_41_Events() => Run(_script, _quarterly10y);

    [Benchmark(Description = "pam-simple: 1 contract, 50y monthly (601 events)")]
    public int Simple_601_Events() => Run(_script, _monthly50y);

    // pam-reference is the script that reproduces all 42 ACTUS reference cases; it does the full
    // PAM semantics per event, so it is the fair counterpart to ACTUS-I. It emits IP at
    // maturity separately, like ACTUS-I: 42 and 602 events.
    [Benchmark(Description = "pam-reference: 1 contract, 10y quarterly (42 events)")]
    public int Reference_42_Events() => Run(_reference, ReferenceInput(_quarterly10y));

    [Benchmark(Description = "pam-reference: 1 contract, 50y monthly (602 events)")]
    public int Reference_602_Events() => Run(_reference, ReferenceInput(_monthly50y));

    private static int Run(CompiledScript<JToken> script, JToken contract)
    {
        // Execute may mutate its input document, so give each call its own copy: the parse/copy
        // cost is part of what a request pays anyway.
        var input = contract.DeepClone();
        var context = JsonExecutionContext.CreateDefault();
        var result = script.Execute(input, context);
        if (!result.Success)
            throw new InvalidOperationException("Contract failed to execute.");
        return result.Data.SelectToken("$.events")!.Count();
    }

    /// <summary>The flat terms of the sample contracts, plus what pam-reference reads for a cycle.</summary>
    public static JToken ReferenceInput(JToken contract)
    {
        var c = (JObject)contract.DeepClone();
        var cycle = (JObject)c["interestPaymentCycle"]!;
        cycle["longStub"] = true;
        c["interestPaymentAnchor"] = DateTime.Parse((string)c["initialExchangeDate"]!, System.Globalization.CultureInfo.InvariantCulture)
            .AddMonths((int)cycle["count"]!).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        c["statusDate"] = c["initialExchangeDate"];
        c["endOfMonth"] = false;
        return c;
    }

    private static JToken Contract(string id, string role, int notional, double rate, string dcc,
        string ied, string md, int months) =>
        new JObject
        {
            ["contractId"] = id,
            ["contractRole"] = role,
            ["currency"] = "EUR",
            ["notionalPrincipal"] = notional,
            ["nominalInterestRate"] = rate,
            ["dayCountConvention"] = dcc,
            ["initialExchangeDate"] = ied,
            ["maturityDate"] = md,
            ["interestPaymentCycle"] = new JObject { ["count"] = months, ["unit"] = "months" },
        };

    private static string ScriptPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "TLio.Sample.Actus.Api")))
            dir = dir.Parent;
        if (dir is null)
            throw new DirectoryNotFoundException("samples/TLio.Sample.Actus.Api is not above the benchmark output.");
        return Path.Combine(dir.FullName, "samples", "TLio.Sample.Actus.Api", "Scripts", name);
    }
}

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
