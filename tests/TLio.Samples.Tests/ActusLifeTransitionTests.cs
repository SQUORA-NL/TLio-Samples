using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace TLio.Samples.Tests;

/// <summary>
/// The transition guards (<c>life-transition.json</c>) against ACTUS-I's
/// <c>LifeInsuranceGpuExecutor.ValidateTransition</c>: all 1,736 checks of the oracle (every Markov
/// transition x 8 contract variants x 16 fact combinations, plus every ordered state pair), the
/// verdict, the rule that decided and the reason text.
/// </summary>
[TestFixture]
public class ActusLifeTransitionTests
{
    /// <summary>Checks the script does not reproduce, with the reason. Empty: all match.</summary>
    private static readonly Dictionary<string, string> KnownGaps = new();

    [Test]
    public void Transitions_MatchActusValidateTransition()
    {
        var section = ActusLifeSupport.Section("transitions");
        var items = ((JArray)section["items"]!).Cast<JObject>().ToList();
        var failures = new List<string>();
        var perRule = new SortedDictionary<string, (int Total, int Pass)>();
        var allowedCount = 0;

        foreach (var item in items)
        {
            var expectedRule = (string)item["ruleId"]!;
            var input = new JObject
            {
                ["contract"] = item["contract"],
                ["from"] = item["from"],
                ["to"] = item["to"],
                ["evalDateDays"] = item["evalDateDays"],
                ["facts"] = item["facts"],
            };
            var (data, error) = ActusLifeSupport.Run("life-transition.json", input);
            var got = data?["result"];
            var expectedAllowed = (bool)item["allowed"]!;
            var expectedReason = (string)item["reason"]!;
            var ok = error is null && got is not null
                     && (bool?)got["allowed"] == expectedAllowed
                     && (string?)got["ruleId"] == expectedRule
                     && (string?)got["reason"] == expectedReason;

            perRule.TryGetValue(expectedRule, out var c);
            perRule[expectedRule] = (c.Total + 1, c.Pass + (ok ? 1 : 0));
            if (ok) { if (expectedAllowed) allowedCount++; continue; }

            failures.Add($"{item["from"]}->{item["to"]} [{item["variant"]}] facts={item["facts"]!.ToString(Newtonsoft.Json.Formatting.None)}: " +
                         $"expected {expectedAllowed}/{expectedRule}/\"{expectedReason}\", got " +
                         (error ?? $"{got?["allowed"]}/{got?["ruleId"]}/\"{got?["reason"]}\""));
        }

        TestContext.WriteLine(ActusLifeSupport.Table(
            $"Transitions: {items.Count - failures.Count}/{items.Count} of ACTUS-I's checks reproduced exactly (verdict, rule, reason); {allowedCount} of them allowed",
            perRule.Select(kv => ("rule", kv.Key, $"{kv.Value.Pass}/{kv.Value.Total}"))));

        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures.Take(12)));
    }
}
