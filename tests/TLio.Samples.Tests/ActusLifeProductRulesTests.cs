using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace TLio.Samples.Tests;

/// <summary>
/// The two per-product rule sets (<c>life-product-rules.json</c>) against ACTUS-I's
/// <c>ProductRuleSet&lt;TMeta&gt;.EvaluateGuard</c>: NovaCover LifestyleProtect and ClearLife
/// SimpleTerm, 300 seeded (meta, contract) combinations each times every rule, so 3,000 checks.
/// Verdict, reason text and the inputs-used provenance must all match.
///
/// One named known gap: ACTUS-I prints a number with .NET's <c>G17</c> (17 significant digits) and
/// TLio prints the shortest text that reads back as the same double. They differ only for a value
/// that is not exactly representable, and the oracle has exactly one: a <c>years_in_force</c> of
/// 0.99f widened from float32, which ACTUS-I prints as 0.99000000953674316 and TLio as
/// 0.9900000095367432. Same number, different digits. TLio's <c>toFixed</c> stops at 15 decimals, so
/// the script cannot reproduce the 17-digit form. The test checks that such a check is different
/// only in that way (every number in both texts parses to the same double) and pins the count, so a
/// TLio change that closes the gap fails this test until the count is updated.
/// </summary>
[TestFixture]
public class ActusLifeProductRulesTests
{
    /// <summary>Checks whose reason text differs only in the trailing digits of a float32-widened number.</summary>
    private const int KnownDigitFormattingGap = 142;

    private static string Normalize(string s) =>
        Regex.Replace(s, @"\d+\.\d{14,}", m =>
            double.Parse(m.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));

    [Test]
    public void ProductRules_MatchActusProductRuleSets()
    {
        var rows = new List<(string Area, string Result, string Detail)>();
        var failures = new List<string>();
        var total = 0;
        var exact = 0;
        var digitGap = 0;

        foreach (var section in ActusLifeSupport.Sections("productRules"))
        {
            var product = (string)section["product"]!;
            var perRule = new SortedDictionary<string, (int Total, int Exact, int Gap, int Allowed)>();

            foreach (var item in ((JArray)section["items"]!).Cast<JObject>())
            {
                total++;
                var ruleId = (string)item["ruleId"]!;
                var input = new JObject
                {
                    ["contract"] = item["contract"],
                    ["meta"] = item["meta"],
                    ["ruleId"] = ruleId,
                };
                var (data, error) = ActusLifeSupport.Run("life-product-rules.json", input);
                var got = data?["result"];
                var expected = (JObject)item["result"]!;

                string? problem = null;
                var onlyDigits = false;
                if (error is not null || got is null) problem = error ?? "no result";
                else if ((bool?)got["allowed"] != (bool)expected["allowed"]!) problem = $"allowed {got["allowed"]} vs {expected["allowed"]}";
                else
                {
                    var eu = (JObject)expected["inputsUsed"]!;
                    var gu = got["inputsUsed"] as JObject;
                    var reasonSame = (string?)got["reason"] == (string)expected["reason"]!;
                    var reasonNorm = Normalize((string?)got["reason"] ?? "") == Normalize((string)expected["reason"]!);
                    var usedSame = gu is not null && gu.Count == eu.Count && eu.Properties().All(p => (string?)gu[p.Name] == (string)p.Value!);
                    var usedNorm = gu is not null && gu.Count == eu.Count && eu.Properties().All(p => Normalize((string?)gu[p.Name] ?? "") == Normalize((string)p.Value!));

                    if (reasonSame && usedSame) { /* exact */ }
                    else if (reasonNorm && usedNorm) onlyDigits = true;
                    else problem = $"reason \"{got["reason"]}\" vs \"{expected["reason"]}\"; inputsUsed {gu?.ToString(Newtonsoft.Json.Formatting.None)} vs {eu.ToString(Newtonsoft.Json.Formatting.None)}";
                }

                perRule.TryGetValue(ruleId, out var c);
                perRule[ruleId] = (c.Total + 1, c.Exact + (problem is null && !onlyDigits ? 1 : 0),
                    c.Gap + (onlyDigits ? 1 : 0), c.Allowed + ((bool)expected["allowed"]! ? 1 : 0));
                if (problem is not null)
                    failures.Add($"{ruleId} meta={item["meta"]!.ToString(Newtonsoft.Json.Formatting.None)}: {problem}");
                else if (onlyDigits) digitGap++;
                else exact++;
            }

            rows.AddRange(perRule.Select(kv => (product.Split(' ').Last().Trim('(', ')'), kv.Key,
                $"{kv.Value.Exact}/{kv.Value.Total} exact, {kv.Value.Gap} digit-format gap ({kv.Value.Allowed} allowed, {kv.Value.Total - kv.Value.Allowed} denied)")));
        }

        TestContext.WriteLine(ActusLifeSupport.Table(
            $"Product rules: {exact}/{total} of ACTUS-I's checks reproduced exactly; {digitGap} more differ only in the trailing digits of a float32-widened 0.99 (G17 vs shortest round-trip); {failures.Count} wrong", rows));

        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures.Take(12)));
        Assert.That(digitGap, Is.EqualTo(KnownDigitFormattingGap),
            "the known number-formatting gap changed; update KnownDigitFormattingGap (and the docs) if TLio now prints G17");
    }
}
