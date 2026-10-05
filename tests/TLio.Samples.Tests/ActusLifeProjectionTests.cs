using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace TLio.Samples.Tests;

/// <summary>
/// The forward projection (<c>life-project.json</c>) against ACTUS-I's <c>LifeProjectionKernel</c>,
/// every step of all 64 oracle contracts in the three configurations the oracle recorded.
///
/// The kernel does its age, duration and table lookups in float32, so this cannot be held to
/// 1e-10. The tolerances come from the oracle's own measurement of a straightforward
/// double-precision reimplementation of the kernel formulas (oracle README, "Precision"): with the
/// tables rebuilt in double, which is what the script evaluates, the largest differences seen were
/// 1.5e-8 in a probability, 3.1e-3 in a monthly cashflow and 5.4e-4 in an annual cashflow.
/// </summary>
[TestFixture]
public class ActusLifeProjectionTests
{
    private const double ProbabilityTolerance = 5e-8;

    private static double CashflowTolerance(int timeSteps, double dt) => dt >= 1.0 ? 2e-3 : 1e-2;

    [Test]
    public void Projection_MatchesActusLifeKernel()
    {
        var rows = new List<(string Area, string Result, string Detail)>();
        var failures = new List<string>();

        foreach (var section in ActusLifeSupport.Sections("projection"))
        {
            var config = (string)section["config"]!;
            var timeSteps = (int)section["timeSteps"]!;
            var dt = (double)section["dtYears"]!;
            var cashTol = CashflowTolerance(timeSteps, dt);
            double maxCash = 0, maxProb = 0;
            int contracts = 0, passed = 0;

            foreach (var item in ((JArray)section["items"]!).Cast<JObject>())
            {
                contracts++;
                var name = (string)item["contract"]!;
                var input = new JObject
                {
                    ["contract"] = ActusLifeSupport.Contract(name),
                    ["timeSteps"] = timeSteps,
                    ["dtYears"] = dt,
                };
                var (data, error) = ActusLifeSupport.Run("life-project.json", input);
                if (error is not null) { failures.Add($"[{config}] {name}: {error}"); continue; }

                var steps = data!["steps"] as JArray;
                if (steps is null || steps.Count != timeSteps)
                {
                    failures.Add($"[{config}] {name}: expected {timeSteps} steps, got {steps?.Count.ToString() ?? "none"}");
                    continue;
                }

                var ok = true;
                foreach (var (channel, tol) in new[]
                {
                    ("expectedCashflow", cashTol), ("probActive", ProbabilityTolerance),
                    ("probDeathClaimed", ProbabilityTolerance), ("probLapsed", ProbabilityTolerance),
                })
                {
                    var expected = (JArray)item[channel]!;
                    for (var t = 0; t < timeSteps; t++)
                    {
                        var diff = System.Math.Abs((double)expected[t] - ActusLifeSupport.D(steps[t][channel]));
                        if (channel == "expectedCashflow") maxCash = System.Math.Max(maxCash, double.IsNaN(diff) ? double.PositiveInfinity : diff);
                        else maxProb = System.Math.Max(maxProb, double.IsNaN(diff) ? double.PositiveInfinity : diff);
                        if (double.IsNaN(diff) || diff > tol)
                        {
                            if (ok) failures.Add($"[{config}] {name} step {t} {channel}: expected {(double)expected[t]:R}, got {ActusLifeSupport.D(steps[t][channel]):R} (diff {diff:E2}, tolerance {tol:E0})");
                            ok = false;
                            break;
                        }
                    }
                }
                if (ok) passed++;
            }

            rows.Add((config, $"{passed}/{contracts}",
                $"max |diff| cashflow {maxCash:E2} (tolerance {cashTol:E0}), probability {maxProb:E2} (tolerance {ProbabilityTolerance:E0})"));
        }

        TestContext.WriteLine(ActusLifeSupport.Table("Projection against ACTUS-I's kernel (CPU accelerator)", rows));
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures.Take(20)));
    }
}
