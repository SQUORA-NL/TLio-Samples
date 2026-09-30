using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TLio.Client;
using TLio.Extensions.Looping;
using TLio.Extensions.Math;
using TLio.Extensions.Text;
using TLio.Extensions.TimeDate;
using TLio.Json;

namespace TLio.Samples.Tests;

/// <summary>
/// The rate floor and cap variant of <c>pam-reference.json</c> (ACTUS RRLF / RRLC): optional
/// <c>rateFloor</c> and <c>rateCap</c> terms bound the rate set at every rate reset and the
/// initial rate. There is no ACTUS reference case for it, so the expected values are worked out by
/// hand from one contract:
///
///   notional 12,000, role RPA, 30E360 (every month is exactly 1/12 year), so one month of
///   interest is rate x 1,000. IED 2020-01-01, maturity 2020-04-01, monthly interest from
///   2020-02-01, monthly rate reset from 2020-02-01, nominal rate 5 %, market rate 1 % observed
///   on 2020-02-01 and 10 % on 2020-03-01.
///
///   Event order (same-day ACTUS priority: IP before RR before MD):
///     IED, IP 02-01, RR 02-01, IP 03-01, RR 03-01, IP 04-01, MD 04-01
///   Each IP pays 1,000 x the rate in force since the previous event; each RR sets the next one.
/// </summary>
[TestFixture]
public class ActusPamRateBoundsTests
{
    private static readonly string[] ExpectedTypes = ["IED", "IP", "RR", "IP", "RR", "IP", "MD"];

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "TLio.Sample.Actus.Api")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples", "TLio.Sample.Actus.Api", "Scripts", "pam-reference.json");
    }

    private static readonly Lazy<CompiledScript<JToken>> Script = new(() =>
    {
        var options = ParseOptions<JToken>.CreateDefault();
        options.FunctionsProvider.RegisterMath<JToken>();
        options.FunctionsProvider.RegisterText<JToken>();
        options.FunctionsProvider.RegisterTimeDate<JToken>();
        options.CommandsProvider.RegisterLooping<JToken>();
        var engine = new ScriptEngine<JToken>(options.CommandsProvider, options.FunctionsProvider);
        return engine.Compile(File.ReadAllText(ScriptPath()), JsonExecutionContext.CreateDefault().NodeAdapter);
    });

    internal static JObject Contract(double? floor, double? cap)
    {
        var c = new JObject
        {
            ["contractId"] = "BOUNDS",
            ["contractRole"] = "RPA",
            ["currency"] = "EUR",
            ["statusDate"] = "2020-01-01",
            ["initialExchangeDate"] = "2020-01-01",
            ["maturityDate"] = "2020-04-01",
            ["notionalPrincipal"] = 12000,
            ["nominalInterestRate"] = 0.05,
            ["dayCountConvention"] = "30E360",
            ["interestPaymentAnchor"] = "2020-02-01",
            ["interestPaymentCycle"] = new JObject { ["count"] = 1, ["unit"] = "months", ["longStub"] = true },
            ["rateResetAnchor"] = "2020-02-01",
            ["rateResetCycle"] = new JObject { ["count"] = 1, ["unit"] = "months", ["longStub"] = true },
            ["marketData"] = new JArray
            {
                new JObject { ["timestamp"] = "2020-02-01", ["value"] = 0.01 },
                new JObject { ["timestamp"] = "2020-03-01", ["value"] = 0.10 },
            },
        };
        if (floor is not null) c["rateFloor"] = floor;
        if (cap is not null) c["rateCap"] = cap;
        return c;
    }

    private static JArray Run(double? floor, double? cap)
    {
        var context = JsonExecutionContext.CreateDefault();
        var result = Script.Value.Execute(Contract(floor, cap), context);
        Assert.That(result.Success, Is.True,
            string.Join("; ", context.GetLogEntries().Select(e => $"{e.Level}: {e.Message}")));
        return (JArray)result.Data["events"]!;
    }

    // floor, cap | rate on the IED event | rate after RR 1, RR 2 | payoffs of IP 1, IP 2, IP 3
    [TestCase(null, null, 0.05, 0.01, 0.10, 50.0, 10.0, 100.0, TestName = "Neither bound set: the plain result")]
    [TestCase(0.005, 0.20, 0.05, 0.01, 0.10, 50.0, 10.0, 100.0, TestName = "Bounds set but neither binds: the plain result")]
    [TestCase(0.02, null, 0.05, 0.02, 0.10, 50.0, 20.0, 100.0, TestName = "Floor binds on the first reset (1 % becomes 2 %)")]
    [TestCase(null, 0.08, 0.05, 0.01, 0.08, 50.0, 10.0, 80.0, TestName = "Cap binds on the second reset (10 % becomes 8 %)")]
    [TestCase(0.02, 0.08, 0.05, 0.02, 0.08, 50.0, 20.0, 80.0, TestName = "Floor and cap both bind")]
    [TestCase(0.06, null, 0.06, 0.06, 0.10, 60.0, 60.0, 100.0, TestName = "Floor above the nominal rate lifts the initial rate too")]
    [TestCase(null, 0.03, 0.03, 0.01, 0.03, 30.0, 10.0, 30.0, TestName = "Cap below the nominal rate lowers the initial rate too")]
    public void Bounds_ShapeTheRateAndTheInterest(
        double? floor, double? cap, double rateAtIed, double rate1, double rate2,
        double ip1, double ip2, double ip3)
    {
        var events = Run(floor, cap);

        Assert.That(events.Select(e => (string)e["type"]!), Is.EqualTo(ExpectedTypes));
        Assert.That(events[0]["payoff"]!.Value<double>(), Is.EqualTo(-12000).Within(1e-9));
        Assert.That(events[0]["nominalInterestRate"]!.Value<double>(), Is.EqualTo(rateAtIed).Within(1e-12), "rate at IED");
        Assert.That(events[1]["payoff"]!.Value<double>(), Is.EqualTo(ip1).Within(1e-9), "IP 2020-02-01");
        Assert.That(events[2]["nominalInterestRate"]!.Value<double>(), Is.EqualTo(rate1).Within(1e-12), "rate after RR 2020-02-01");
        Assert.That(events[3]["payoff"]!.Value<double>(), Is.EqualTo(ip2).Within(1e-9), "IP 2020-03-01");
        Assert.That(events[4]["nominalInterestRate"]!.Value<double>(), Is.EqualTo(rate2).Within(1e-12), "rate after RR 2020-03-01");
        Assert.That(events[5]["payoff"]!.Value<double>(), Is.EqualTo(ip3).Within(1e-9), "IP 2020-04-01");
        Assert.That(events[6]["payoff"]!.Value<double>(), Is.EqualTo(12000).Within(1e-9), "MD returns the principal");
    }

    [Test]
    public void AbsentBounds_ChangeNothing_ComparedWithVeryWideBounds()
    {
        // Same contract, no bounds against bounds nobody can reach: the two must be identical, field
        // for field, so the variant adds no behaviour until a term switches it on.
        var plain = Run(null, null);
        var wide = Run(-5.0, 5.0);
        Assert.That(JToken.DeepEquals(plain, wide), Is.True, plain + Environment.NewLine + wide);
    }
}
