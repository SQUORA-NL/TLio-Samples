using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
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
/// <c>pam-reference.json</c> against ACTUS-I's own PAM engine, on the golden data in
/// <c>Resources/oracle/pam-oracle.json</c>: the 42 published reference cases plus 400 seeded
/// generated contracts, each run through <c>ActusInsurance.Core.CPU</c> (<c>Schedule</c> then
/// <c>Apply</c>). "Match" means the same events in the same order (count, date, type) and, to ten
/// decimals (actual rounded to 10, tolerance 2e-10, as ACTUS-I's own tests), the same payoff,
/// notional, nominal rate, accrued interest and accrued fee.
///
/// Cases where ACTUS-I throws (an unsupported cycle such as <c>P2WL0</c>) must fail in the script
/// too, rather than yield an invented schedule.
///
/// The input mapping hands the script the ACTUS terms as they are: raw cycle strings
/// (<c>cycleOfInterestPayment: "P1M15DL1"</c>), raw role, day count and business-day names. The
/// script does its own parsing, so an unsupported form fails in the script.
///
/// Cases the script cannot reproduce are named in <see cref="KnownGaps"/> with the reason. They stay
/// in the run and in the printed table; a case that starts passing fails the test until it is removed.
/// </summary>
[TestFixture]
public class ActusPamOracleTests
{
    private const double Tolerance = 2e-10;

    /// <summary>Cases pam-reference.json does not reproduce, with the reason.</summary>
    private static readonly Dictionary<string, string> KnownGaps = new()
    {
    };

    private static readonly ConcurrentDictionary<string, Row> Rows = new();

    private sealed class Row
    {
        public string[] Features = [];
        public string? Difference;   // null = pass
    }

    // ── Data ─────────────────────────────────────────────────────────────────────

    private static readonly string OraclePath =
        Path.Combine(AppContext.BaseDirectory, "Resources", "oracle", "pam-oracle.json");

    // Dates must stay strings: with Newtonsoft's default they become DateTime and ToString() then
    // renders them in the current culture.
    private static readonly Lazy<JObject> Oracle = new(() =>
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(OraclePath)))
        {
            DateParseHandling = DateParseHandling.None,
        };
        return JObject.Load(reader);
    });

    internal static readonly Lazy<Dictionary<string, JObject>> Cases = new(() =>
        Oracle.Value["cases"]!.Cast<JObject>().ToDictionary(c => c["id"]!.ToString(), c => c));

    public static IEnumerable<string> CaseIds() =>
        Oracle.Value["cases"]!.Select(c => c["id"]!.ToString());

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "TLio.Sample.Actus.Api")))
            dir = dir.Parent;
        var root = dir?.FullName
            ?? throw new DirectoryNotFoundException("samples/TLio.Sample.Actus.Api is not above the test directory.");
        return Path.Combine(root, "samples", "TLio.Sample.Actus.Api", "Scripts", "pam-reference.json");
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

    // ── ACTUS terms -> script input ──────────────────────────────────────────────

    private static string? Str(JObject terms, string key)
    {
        var v = terms[key];
        if (v is null || v.Type == JTokenType.Null) return null;
        // Invariant culture on purpose: JValue.ToString() renders 0.05 as "0,05" on a Dutch machine.
        var s = (v is JValue jv ? Convert.ToString(jv.Value, CultureInfo.InvariantCulture) : v.ToString())?.Trim();
        return string.IsNullOrEmpty(s) || s == "None" ? null : s;
    }

    private static double? Num(JObject terms, string key) =>
        Str(terms, key) is { } s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>ISO date, keeping the time only when it is not midnight.</summary>
    private static string? Date(JObject terms, string key)
    {
        if (Str(terms, key) is not { } s) return null;
        var d = DateTime.Parse(s, CultureInfo.InvariantCulture);
        return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                            : d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static void Put(JObject o, string key, object? value)
    {
        if (value is not null) o[key] = JToken.FromObject(value);
    }

    private static JArray Series(JObject testCase, string? code)
    {
        var rows = new JArray();
        if (code is null || testCase["dataObserved"]?[code] is not JArray data) return rows;
        foreach (var d in data)
            rows.Add(new JObject
            {
                ["timestamp"] = DateTime.Parse(d["timestamp"]!.Value<string>()!, CultureInfo.InvariantCulture)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                // Read the number, never its ToString(): that is culture-dependent (0,0265 on a Dutch machine).
                ["value"] = d["value"]!.Value<double>(),
            });
        return rows;
    }

    /// <summary>The ACTUS terms of one case as the flat input document the script reads.</summary>
    internal static JObject BuildInput(JObject testCase)
    {
        var t = (JObject)testCase["terms"]!;
        var input = new JObject();
        Put(input, "contractId", Str(t, "contractID"));
        Put(input, "contractRole", Str(t, "contractRole"));
        Put(input, "currency", Str(t, "currency"));
        Put(input, "statusDate", Date(t, "statusDate"));
        Put(input, "initialExchangeDate", Date(t, "initialExchangeDate"));
        Put(input, "maturityDate", Date(t, "maturityDate"));
        Put(input, "notionalPrincipal", Num(t, "notionalPrincipal"));
        Put(input, "nominalInterestRate", Num(t, "nominalInterestRate"));
        Put(input, "premiumDiscountAtIED", Num(t, "premiumDiscountAtIED"));
        Put(input, "accruedInterest", Num(t, "accruedInterest"));
        Put(input, "dayCountConvention", Str(t, "dayCountConvention"));
        Put(input, "endOfMonth", Str(t, "endOfMonthConvention") == "EOM");
        Put(input, "businessDayConvention", Str(t, "businessDayConvention"));
        Put(input, "calendar", Str(t, "calendar"));

        Put(input, "interestPaymentAnchor", Date(t, "cycleAnchorDateOfInterestPayment"));
        Put(input, "cycleOfInterestPayment", Str(t, "cycleOfInterestPayment"));
        Put(input, "capitalizationEndDate", Date(t, "capitalizationEndDate"));

        Put(input, "rateResetAnchor", Date(t, "cycleAnchorDateOfRateReset"));
        Put(input, "cycleOfRateReset", Str(t, "cycleOfRateReset"));
        Put(input, "rateSpread", Num(t, "rateSpread"));
        Put(input, "rateMultiplier", Num(t, "rateMultiplier"));
        Put(input, "nextResetRate", Num(t, "nextResetRate"));
        Put(input, "lifeFloor", Num(t, "lifeFloor"));
        Put(input, "lifeCap", Num(t, "lifeCap"));
        Put(input, "periodFloor", Num(t, "periodFloor"));
        Put(input, "periodCap", Num(t, "periodCap"));
        var rrCode = Str(t, "marketObjectCodeOfRateReset");
        if (rrCode is not null)
        {
            input["marketObjectCodeOfRateReset"] = rrCode;
            input["marketData"] = Series(testCase, rrCode);
        }

        Put(input, "feeRate", Num(t, "feeRate"));
        Put(input, "feeBasis", Str(t, "feeBasis"));
        Put(input, "feeAccrued", Num(t, "feeAccrued"));
        Put(input, "feeAnchor", Date(t, "cycleAnchorDateOfFee"));
        Put(input, "cycleOfFee", Str(t, "cycleOfFee"));

        Put(input, "purchaseDate", Date(t, "purchaseDate"));
        Put(input, "priceAtPurchaseDate", Num(t, "priceAtPurchaseDate"));
        Put(input, "terminationDate", Date(t, "terminationDate"));
        Put(input, "priceAtTerminationDate", Num(t, "priceAtTerminationDate"));

        Put(input, "scalingEffect", Str(t, "scalingEffect"));
        Put(input, "scalingAnchor", Date(t, "cycleAnchorDateOfScalingIndex"));
        Put(input, "cycleOfScalingIndex", Str(t, "cycleOfScalingIndex"));
        Put(input, "scalingIndexAtContractDealDate", Num(t, "scalingIndexAtContractDealDate"));
        Put(input, "notionalScalingMultiplier", Num(t, "notionalScalingMultiplier"));
        Put(input, "interestScalingMultiplier", Num(t, "interestScalingMultiplier"));
        var scCode = Str(t, "marketObjectCodeOfScalingIndex");
        if (scCode is not null)
        {
            input["marketObjectCodeOfScalingIndex"] = scCode;
            input["scalingData"] = Series(testCase, scCode);
        }
        return input;
    }

    // ── Run and compare ──────────────────────────────────────────────────────────

    private static (JArray? Events, string? Error) Run(JObject input)
    {
        try
        {
            var context = JsonExecutionContext.CreateDefault();
            var result = Script.Value.Execute(input, context);
            if (!result.Success)
            {
                var reasons = string.Join("; ", context.GetLogEntries()
                    .Where(e => e.Level.ToString() == "Error").Take(2).Select(e => e.Message));
                return (null, $"script failed: {reasons}");
            }
            return result.Data.SelectToken("$.events") is JArray events
                ? (events, null)
                : (null, "script produced no events array");
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static double Round10(double v) => System.Math.Round(v, 10, MidpointRounding.ToEven);

    private static List<string> Compare(JArray expected, JArray? actual)
    {
        var diffs = new List<string>();
        if (actual is null) { diffs.Add("no events"); return diffs; }

        if (actual.Count != expected.Count)
        {
            var e = string.Join(",", expected.Select(x => $"{x["eventType"]}@{x["eventDate"]!.ToString()[..10]}"));
            var a = string.Join(",", actual.Select(x => $"{x["type"]}@{x["date"]}"));
            diffs.Add($"event count {actual.Count} != expected {expected.Count} | expected {Trim(e, 220)} | actual {Trim(a, 220)}");
            return diffs;
        }

        void CheckNum(int i, string field, JToken? exp, JToken? act)
        {
            if (exp is null || exp.Type == JTokenType.Null) return;
            if (act is null || act.Type == JTokenType.Null) { diffs.Add($"[{i}] {field}: not emitted"); return; }
            var e = Round10(exp.Value<double>());
            var a = Round10(act.Value<double>());
            if (double.IsNaN(a) || System.Math.Abs(e - a) > Tolerance)
                diffs.Add($"[{i}] {field}: expected {e.ToString(CultureInfo.InvariantCulture)} got {a.ToString(CultureInfo.InvariantCulture)}");
        }

        for (var i = 0; i < expected.Count; i++)
        {
            var exp = expected[i];
            var act = actual[i];

            var expType = exp["eventType"]!.ToString();
            var actType = act["type"]?.ToString();
            if (!string.Equals(expType, actType, StringComparison.Ordinal))
                diffs.Add($"[{i}] type: expected {expType} got {actType ?? "not emitted"}");

            var expDate = exp["eventDate"]!.ToString()[..10];
            var actDate = act["date"]?.ToString();
            actDate = actDate is { Length: >= 10 } ? actDate[..10] : actDate;
            if (!string.Equals(expDate, actDate, StringComparison.Ordinal))
                diffs.Add($"[{i}] date: expected {expDate} got {actDate ?? "not emitted"}");

            CheckNum(i, "payoff", exp["payoff"], act["payoff"]);
            CheckNum(i, "notionalPrincipal", exp["notionalPrincipal"], act["notionalPrincipal"]);
            CheckNum(i, "nominalInterestRate", exp["nominalInterestRate"], act["nominalInterestRate"]);
            CheckNum(i, "accruedInterest", exp["accruedInterest"], act["accruedInterest"]);
            CheckNum(i, "feeAccrued", exp["feeAccrued"], act["feeAccrued"]);
        }
        return diffs;
    }

    private static string Trim(string s, int n = 90) => s.Length <= n ? s : s[..n] + "...";

    /// <summary>
    /// The ACTUS features a case exercises: the row labels of the printed table. A case counts in every
    /// row it belongs to, so the rows add up to more than the number of cases.
    /// </summary>
    private static string[] Features(JObject testCase)
    {
        var t = (JObject)testCase["terms"]!;
        var tags = new List<string>();
        tags.Add(testCase["error"] is not null ? "error case (ACTUS-I throws)"
            : testCase["id"]!.ToString().StartsWith("gen", StringComparison.Ordinal) ? "generated" : "reference (42)");
        if (Str(t, "cycleOfRateReset") is not null) tags.Add(Str(t, "nextResetRate") is null ? "rate reset" : "rate reset with RRF");
        if (Num(t, "lifeFloor") is not null || Num(t, "lifeCap") is not null || Num(t, "periodFloor") is not null || Num(t, "periodCap") is not null)
            tags.Add("life/period caps and floors");
        if (Str(t, "scalingEffect") is not null) tags.Add("scaling");
        if (Str(t, "cycleOfFee") is not null) tags.Add("fees");
        if (Str(t, "purchaseDate") is not null) tags.Add("purchase");
        if (Str(t, "terminationDate") is not null) tags.Add("termination");
        if (Str(t, "capitalizationEndDate") is not null) tags.Add("IPCI");
        if (Str(t, "businessDayConvention") is { } b && b != "NOS" && Str(t, "calendar") is "MF" or "MFH") tags.Add("business days");
        if (Str(t, "accruedInterest") is not null) tags.Add("initial accrual given");
        if (Str(t, "cycleAnchorDateOfInterestPayment") is { } a && Str(t, "initialExchangeDate") is { } ied
            && DateTime.Parse(a, CultureInfo.InvariantCulture) < DateTime.Parse(ied, CultureInfo.InvariantCulture)) tags.Add("anchor before IED");
        if (Str(t, "dayCountConvention") is { } dc) tags.Add("day count " + dc);
        if (Str(t, "contractRole") is { } r && r is not ("RPA" or "RPL")) tags.Add("role " + r);
        return [.. tags];
    }

    // ── Test ─────────────────────────────────────────────────────────────────────

    [TestCaseSource(nameof(CaseIds))]
    public void PamReference_MatchesActusI(string caseId)
    {
        var testCase = Cases.Value[caseId];
        var (events, error) = Run(BuildInput(testCase));

        List<string> diffs;
        if (testCase["error"] is not null)
        {
            // ACTUS-I threw: the script must not invent a schedule.
            diffs = error is null ? [$"ACTUS-I fails ({Trim(testCase["error"]!.ToString())}) but the script produced {events!.Count} events"] : [];
        }
        else
        {
            diffs = error is null ? Compare((JArray)testCase["events"]!, events) : [error];
        }

        Rows[caseId] = new Row { Features = Features(testCase), Difference = diffs.Count == 0 ? null : diffs[0] };

        if (KnownGaps.TryGetValue(caseId, out var reason))
        {
            Assert.That(diffs, Is.Not.Empty,
                $"{caseId} now passes: remove it from {nameof(KnownGaps)} (was: {reason}).");
            Assert.Warn($"{caseId} known gap: {reason} | first difference: {diffs[0]}");
        }
        else
        {
            Assert.That(diffs, Is.Empty, $"{caseId}: {string.Join(" | ", diffs.Take(4))}");
        }
    }

    [OneTimeTearDown]
    public void PrintTable()
    {
        if (Rows.IsEmpty) return;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("pam-reference.json against ACTUS-I (pam-oracle.json), to 10 decimals");
        sb.AppendLine("feature (a case counts in every row it belongs to)  | passing / cases");
        sb.AppendLine("----------------------------------------------------|----------------");
        foreach (var g in Rows.Values.SelectMany(r => r.Features.Select(f => (Feature: f, r.Difference))).GroupBy(x => x.Feature).OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine($"{g.Key,-51} | {g.Count(x => x.Difference is null),4} / {g.Count(),-4}");
        sb.AppendLine($"all cases                                           | {Rows.Values.Count(r => r.Difference is null),4} / {Rows.Count}");
        var failing = Rows.Where(r => r.Value.Difference is not null).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        if (failing.Count > 0)
        {
            sb.AppendLine("first difference of every case that does not match:");
            foreach (var f in failing) sb.AppendLine($"  {f.Key,-8} {Trim(f.Value.Difference!, 200)}");
        }
        TestContext.Progress.WriteLine(sb.ToString());
    }
}
