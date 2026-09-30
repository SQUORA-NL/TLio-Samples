using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace TLio.Samples.Tests;

/// <summary>
/// Scenarios and portfolio aggregation against ACTUS-I's <c>ProjectScenarioBatch</c> and
/// <c>LifeProjectionCube</c>. For each of the 7 scenario sets in the oracle (10 contracts x S
/// scenarios x 30 or 36 steps):
///  1. <c>life-scenario.json</c> applies the scenario to each contract (mortality shift into
///     extraPremBps, lapse multiplier into yearsInForce);
///  2. <c>life-project.json</c> projects the adjusted contract;
///  3. <c>life-aggregate.json</c> builds the cube's aggregations from those projections.
/// Every projected channel, the per-contract totals and survival probabilities, the portfolio
/// cashflow at each step, the scenario totals and both min/max/mean statistics are compared.
///
/// What is NOT done in script: <c>LifeScenarioSet.GenerateDeterministic</c> draws its scenarios
/// with an xorshift64 generator (64-bit unsigned shifts and xors), which TLio has no operators for.
/// The scenario lists are therefore taken from the oracle as input data, exactly as a host would
/// pass in a scenario set; the generator itself is not exercised.
/// </summary>
[TestFixture]
public class ActusLifeScenarioTests
{
    private const double ProbabilityTolerance = 5e-8;

    [Test]
    public void Scenarios_And_Aggregations_MatchActusLifeCube()
    {
        var rows = new List<(string Area, string Result, string Detail)>();
        var failures = new List<string>();

        foreach (var set in ActusLifeSupport.Sections("scenarios"))
        {
            var setName = (string)set["setName"]!;
            var timeSteps = (int)set["timeSteps"]!;
            var dt = (double)set["dtYears"]!;
            var cashTol = dt >= 1.0 ? 2e-3 : 1e-2;
            var scenarios = ((JArray)set["scenarios"]!).Cast<JObject>().ToList();
            var items = ((JArray)set["items"]!).Cast<JObject>().ToList();
            var contractNames = items.Select(i => (string)i["contract"]!).Distinct().ToList();
            var S = scenarios.Count;
            var C = contractNames.Count;
            double maxStep = 0, maxProb = 0;

            // 1 + 2: scenario applied, contract projected; collected as the cube the aggregator takes.
            var cube = new JArray();
            var stepFailure = false;
            for (var s = 0; s < S; s++)
            {
                var perContract = new JArray();
                for (var c = 0; c < C; c++)
                {
                    var name = contractNames[c];
                    var contract = ActusLifeSupport.Contract(name);
                    var (adj, adjError) = ActusLifeSupport.Run("life-scenario.json", new JObject
                    {
                        ["contract"] = contract,
                        ["scenario"] = scenarios[s],
                    });
                    if (adjError is not null) { failures.Add($"[{setName}] {name} scenario {s}: {adjError}"); stepFailure = true; continue; }
                    contract["extraPremBps"] = adj!["adjusted"]!["extraPremBps"];
                    contract["yearsInForce"] = adj["adjusted"]!["yearsInForce"];

                    var (proj, projError) = ActusLifeSupport.Run("life-project.json", new JObject
                    {
                        ["contract"] = contract, ["timeSteps"] = timeSteps, ["dtYears"] = dt,
                    });
                    if (projError is not null) { failures.Add($"[{setName}] {name} scenario {s}: {projError}"); stepFailure = true; continue; }

                    var steps = (JArray)proj!["steps"]!;
                    var expected = items.Single(i => (string)i["contract"]! == name && (int)i["scenario"]! == s);
                    foreach (var (channel, tol) in new[] { ("expectedCashflow", cashTol), ("probActive", ProbabilityTolerance),
                                 ("probDeathClaimed", ProbabilityTolerance), ("probLapsed", ProbabilityTolerance) })
                    {
                        var exp = (JArray)expected[channel]!;
                        for (var t = 0; t < timeSteps; t++)
                        {
                            var diff = System.Math.Abs((double)exp[t] - ActusLifeSupport.D(steps[t][channel]));
                            if (channel == "expectedCashflow") maxStep = System.Math.Max(maxStep, diff); else maxProb = System.Math.Max(maxProb, diff);
                            if (double.IsNaN(diff) || diff > tol)
                            {
                                failures.Add($"[{setName}] {name} scenario {s} step {t} {channel}: expected {(double)exp[t]:R}, got {ActusLifeSupport.D(steps[t][channel]):R}");
                                stepFailure = true;
                                break;
                            }
                        }
                    }
                    perContract.Add(new JObject
                    {
                        ["contract"] = name,
                        ["expectedCashflow"] = new JArray(steps.Select(x => x["expectedCashflow"])),
                        ["probActive"] = new JArray(steps.Select(x => x["probActive"])),
                    });
                }
                cube.Add(perContract);
            }
            if (stepFailure) { rows.Add((setName, "FAIL", "projection differs, aggregation not checked")); continue; }

            // 3: aggregation.
            var (agg, aggError) = ActusLifeSupport.Run("life-aggregate.json", new JObject { ["scenarios"] = cube });
            if (aggError is not null) { failures.Add($"[{setName}] aggregate: {aggError}"); rows.Add((setName, "FAIL", aggError)); continue; }

            double maxTotal = 0, maxAt = 0, maxPortTotal = 0, maxStats = 0, maxSurv = 0;
            for (var s = 0; s < S; s++)
                for (var c = 0; c < C; c++)
                {
                    var expected = items.Single(i => (string)i["contract"]! == contractNames[c] && (int)i["scenario"]! == s);
                    var got = agg!["scenarios"]![s]![c]!;
                    maxTotal = System.Math.Max(maxTotal, System.Math.Abs((double)expected["totalCashflow"]! - ActusLifeSupport.D(got["totalCashflow"])));
                    maxSurv = System.Math.Max(maxSurv, System.Math.Abs((double)expected["survivalProbability"]! - ActusLifeSupport.D(got["survivalProbability"])));
                }

            var at = (JArray)agg!["aggregate"]!["portfolioCashflowAt"]!;
            var expAt = (JArray)set["portfolioCashflowAt"]!;
            for (var s = 0; s < S; s++)
                for (var t = 0; t < timeSteps; t++)
                    maxAt = System.Math.Max(maxAt, System.Math.Abs((double)expAt[s][t]! - ActusLifeSupport.D(at[s * timeSteps + t])));

            var portTotal = (JArray)agg["aggregate"]!["portfolioTotalCashflow"]!;
            var expPortTotal = (JArray)set["portfolioTotalCashflow"]!;
            for (var s = 0; s < S; s++)
                maxPortTotal = System.Math.Max(maxPortTotal, System.Math.Abs((double)expPortTotal[s] - ActusLifeSupport.D(portTotal[s])));

            var perContractStats = (JArray)agg["aggregate"]!["cashflowStatsPerContract"]!;
            var expPerContract = (JArray)set["cashflowStatsPerContract"]!;
            for (var c = 0; c < C; c++)
                foreach (var f in new[] { "min", "max", "mean" })
                    maxStats = System.Math.Max(maxStats, System.Math.Abs((double)expPerContract[c][f]! - ActusLifeSupport.D(perContractStats[c][f])));
            var portStats = agg["aggregate"]!["portfolioCashflowStats"]!;
            foreach (var f in new[] { "min", "max", "mean" })
                maxStats = System.Math.Max(maxStats, System.Math.Abs((double)set["portfolioCashflowStats"]![f]! - ActusLifeSupport.D(portStats[f])));

            // Sums of up to C x T step differences, each within its own tolerance.
            var totalTol = timeSteps * cashTol;
            var atTol = C * cashTol;
            var portTotalTol = C * timeSteps * cashTol;
            var bad = new List<string>();
            if (!(maxTotal <= totalTol)) bad.Add($"totalCashflow {maxTotal:E2} > {totalTol:E1}");
            if (!(maxSurv <= ProbabilityTolerance)) bad.Add($"survivalProbability {maxSurv:E2} > {ProbabilityTolerance:E0}");
            if (!(maxAt <= atTol)) bad.Add($"portfolioCashflowAt {maxAt:E2} > {atTol:E1}");
            if (!(maxPortTotal <= portTotalTol)) bad.Add($"portfolioTotalCashflow {maxPortTotal:E2} > {portTotalTol:E1}");
            if (!(maxStats <= portTotalTol)) bad.Add($"stats {maxStats:E2} > {portTotalTol:E1}");
            failures.AddRange(bad.Select(b => $"[{setName}] {b}"));

            rows.Add((setName, bad.Count == 0 ? $"{C * S}/{C * S}" : "FAIL",
                $"{S} scenarios x {C} contracts x {timeSteps} steps; max |diff| step cashflow {maxStep:E1}, probability {maxProb:E1}, contract total {maxTotal:E1}, survival {maxSurv:E1}, portfolio at t {maxAt:E1}, scenario total {maxPortTotal:E1}, stats {maxStats:E1}"));
        }

        TestContext.WriteLine(ActusLifeSupport.Table("Scenarios and aggregation against ACTUS-I's cube", rows));
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures.Take(20)));
    }
}
