using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace TLio.Samples.Tests;

/// <summary>
/// The eight life-insurance risk factors (<c>life-factors.json</c>) against ACTUS-I's
/// <c>LifeFactorEvaluator</c>, over the 64 contracts of the oracle. That evaluator is plain double
/// arithmetic, so this is held to 1e-10 (absolute).
/// </summary>
[TestFixture]
public class ActusLifeFactorsTests
{
    private const double Tolerance = 1e-10;

    private static readonly string[] Factors =
    [
        "factorAge", "factorSmokerLoading", "factorBaseMortalityQx", "factorAdjustedMortalityQx",
        "factorLapseRate", "factorDisabilityIncidence", "factorBenefitAmount", "factorAnnualPremium",
    ];

    [Test]
    public void Factors_MatchActusLifeFactorEvaluator()
    {
        var items = ((JArray)ActusLifeSupport.Section("factors")["items"]!).Cast<JObject>().ToList();
        var maxDiff = Factors.ToDictionary(f => f, _ => 0.0);
        var failures = new List<string>();

        foreach (var item in items)
        {
            var name = (string)item["contract"]!["name"]!;
            var input = new JObject
            {
                ["contract"] = item["contract"],
                ["evalDateDays"] = item["evalDateDays"],
                ["insuredDobDays"] = item["insuredDobDays"],
            };
            var (data, error) = ActusLifeSupport.Run("life-factors.json", input);
            if (error is not null) { failures.Add($"{name}: {error}"); continue; }

            foreach (var f in Factors)
            {
                var expected = (double)item["factors"]![f]!;
                var actual = ActusLifeSupport.D(data!["factors"]?[f]);
                var diff = System.Math.Abs(expected - actual);
                if (double.IsNaN(diff) || diff > Tolerance)
                    failures.Add($"{name} {f}: expected {expected:R}, got {actual:R}");
                else if (diff > maxDiff[f])
                    maxDiff[f] = diff;
            }
        }

        TestContext.WriteLine(ActusLifeSupport.Table(
            $"Factors: {items.Count - failures.Select(x => x.Split(':')[0].Split(' ')[0]).Distinct().Count()}/{items.Count} contracts pass (tolerance {Tolerance:E0})",
            Factors.Select(f => ("factor", f, $"max abs difference {maxDiff[f]:E2}"))));

        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures.Take(15)));
    }
}
