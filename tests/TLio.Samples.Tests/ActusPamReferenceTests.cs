using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
/// The 42 ACTUS PAM reference cases (<c>Resources/actus-tests-pam.json</c>, the same file ACTUS-I's
/// own tests are held to) run against the Tlio PAM scripts, to ten decimal places: every event's
/// date, type, payoff, notional, nominal rate and accrued interest, and the event count and order.
///
/// Two scripts, two questions:
///  * <c>pam-simple.json</c>, exactly as the sample and the portfolio benchmark use it. It covers
///    IED, IP and MD only, so this is an honest baseline of how much of the reference set it
///    already satisfies (<see cref="PamSimple_Baseline"/>).
///  * <c>pam-reference.json</c>, the same idea grown to ACTUS PAM semantics (rate reset, fees,
///    capitalisation, purchase, termination, business-day and end-of-month conventions)
///    (<see cref="PamReference_MatchesActusReferenceCases"/>).
///
/// The comparison mirrors ACTUS-I's <c>PamTestsGpu</c>: the actual value is rounded to 10 decimals
/// and must be within 2e-10 of the (also rounded) expected value.
/// Cases that do not pass yet are named in <see cref="KnownFailures"/> with the reason. They stay
/// in the run: the suite is green, the gap is visible in the printed table, and a case that starts
/// passing fails the test until it is removed from the list.
/// </summary>
[TestFixture]
public class ActusPamReferenceTests
{
    private const double Tolerance = 2e-10;

    /// <summary>
    /// The reference file's <c>nominalInterestRate</c> column is 0.0 on these cases although their
    /// own terms say 0.1 (the payoffs use 0.1). ACTUS-I skips that column for the same reason
    /// ("known inconsistency"); here the exclusion is limited to exactly these cases.
    /// </summary>
    private static readonly HashSet<string> RateColumnInconsistent =
        ["pam29", "pam30", "pam31", "pam32", "pam33", "pam34", "pam35"];

    /// <summary>Cases pam-reference.json does not reproduce yet, with the reason.</summary>
    private static readonly Dictionary<string, string> KnownFailures = new()
    {
    };

    /// <summary>
    /// Cases pam-simple.json satisfies as-is under the strict comparison (all six fields). It emits
    /// only date, type and payoff, so none can.
    /// </summary>
    private static readonly HashSet<string> BaselineStrictPasses = [];

    /// <summary>Cases pam-simple.json satisfies on the fields it does emit (date, type, payoff of every event).</summary>
    private static readonly HashSet<string> BaselineDtpPasses = [];

    /// <summary>Cases whose money-moving events (non-zero payoff) pam-simple.json reproduces: date, type and payoff.</summary>
    private static readonly HashSet<string> BaselineCashPasses = ["pam36", "pam37"];

    private static readonly ConcurrentDictionary<string, Row> Rows = new();

    private sealed class Row
    {
        public string? BaselineStrict;   // null = pass, otherwise the first difference
        public string? BaselineDtp;
        public string? BaselineCash;
        public string? Reference;
        public bool BaselineRan;
        public bool ReferenceRan;
    }

    // ── Test data ────────────────────────────────────────────────────────────────

    private static readonly string ResourcePath =
        Path.Combine(AppContext.BaseDirectory, "Resources", "actus-tests-pam.json");

    // Dates must stay strings: with Newtonsoft's default they become DateTime and ToString() then
    // renders them in the current culture (30/12/2012 on a Dutch machine).
    private static readonly Lazy<JObject> ReferenceCases = new(() =>
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(ResourcePath)))
        {
            DateParseHandling = DateParseHandling.None,
        };
        return JObject.Load(reader);
    });

    public static IEnumerable<string> CaseIds() =>
        ReferenceCases.Value.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);

    private static string ScriptPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "TLio.Sample.Actus.Api")))
            dir = dir.Parent;
        var root = dir?.FullName
            ?? throw new DirectoryNotFoundException("samples/TLio.Sample.Actus.Api is not above the test directory.");
        return Path.Combine(root, "samples", "TLio.Sample.Actus.Api", "Scripts", name);
    }

    private static CompiledScript<JToken> Compile(string scriptName)
    {
        var options = ParseOptions<JToken>.CreateDefault();
        options.FunctionsProvider.RegisterMath<JToken>();
        options.FunctionsProvider.RegisterText<JToken>();
        options.FunctionsProvider.RegisterTimeDate<JToken>();
        options.CommandsProvider.RegisterLooping<JToken>();
        var engine = new ScriptEngine<JToken>(options.CommandsProvider, options.FunctionsProvider);
        var adapter = JsonExecutionContext.CreateDefault().NodeAdapter;
        return engine.Compile(File.ReadAllText(ScriptPath(scriptName)), adapter);
    }

    // ── Terms -> Tlio input ──────────────────────────────────────────────────────

    private static string? Str(JObject terms, string key)
    {
        var v = terms[key];
        if (v is null || v.Type == JTokenType.Null) return null;
        // Invariant culture on purpose: JValue.ToString() renders 0.05 as "0,05" on a Dutch machine,
        // and double.Parse then reads that as 5 (the comma is taken for a thousands separator).
        var s = (v is JValue jv ? Convert.ToString(jv.Value, CultureInfo.InvariantCulture) : v.ToString())?.Trim();
        return string.IsNullOrEmpty(s) || s == "None" ? null : s;
    }

    private static double? Num(JObject terms, string key) =>
        Str(terms, key) is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : null;

    /// <summary>ISO date, keeping the time only when it is not midnight (pam25 matures at 23:59:59).</summary>
    private static string? Date(JObject terms, string key)
    {
        if (Str(terms, key) is not { } s) return null;
        var d = DateTime.Parse(s, CultureInfo.InvariantCulture);
        return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                            : d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>"P1ML0" -> { count: 1, unit: "months", longStub: true }. Stub 0 is the long stub, 1 the short one.</summary>
    private static JObject? Cycle(JObject terms, string key)
    {
        if (Str(terms, key) is not { } s) return null;
        var m = Regex.Match(s, @"^P(\d+)([DWMQHY])L([01])$");
        if (!m.Success) throw new FormatException($"Unsupported cycle '{s}'.");
        var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var (count, unit) = m.Groups[2].Value switch
        {
            "D" => (n, "days"),
            "W" => (n * 7, "days"),
            "M" => (n, "months"),
            "Q" => (n * 3, "months"),
            "H" => (n * 6, "months"),
            _ => (n, "years"),
        };
        return new JObject { ["count"] = count, ["unit"] = unit, ["longStub"] = m.Groups[3].Value == "0" };
    }

    private static void Put(JObject o, string key, object? value)
    {
        if (value is not null) o[key] = JToken.FromObject(value);
    }

    /// <summary>The ACTUS terms of one case as the flat input document the scripts read.</summary>
    private static JObject BuildInput(JObject testCase)
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
        Put(input, "interestPaymentCycle", Cycle(t, "cycleOfInterestPayment"));
        Put(input, "capitalizationEndDate", Date(t, "capitalizationEndDate"));

        Put(input, "rateResetAnchor", Date(t, "cycleAnchorDateOfRateReset"));
        Put(input, "rateResetCycle", Cycle(t, "cycleOfRateReset"));
        Put(input, "rateSpread", Num(t, "rateSpread"));
        Put(input, "rateMultiplier", Num(t, "rateMultiplier"));
        if (Str(t, "marketObjectCodeOfRateReset") is { } code)
        {
            input["marketObjectCodeOfRateReset"] = code;
            var observed = new JArray();
            if (testCase["dataObserved"]?[code]?["data"] is JArray data)
                foreach (var d in data)
                    observed.Add(new JObject
                    {
                        ["timestamp"] = DateTime.Parse(d["timestamp"]!.ToString(), CultureInfo.InvariantCulture)
                            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["value"] = double.Parse(d["value"]!.ToString(), CultureInfo.InvariantCulture),
                    });
            input["marketData"] = observed;
        }

        Put(input, "feeRate", Num(t, "feeRate"));
        Put(input, "feeBasis", Str(t, "feeBasis"));
        Put(input, "feeAccrued", Num(t, "feeAccrued"));
        Put(input, "feeAnchor", Date(t, "cycleAnchorDateOfFee"));
        Put(input, "feeCycle", Cycle(t, "cycleOfFee"));

        Put(input, "purchaseDate", Date(t, "purchaseDate"));
        Put(input, "priceAtPurchaseDate", Num(t, "priceAtPurchaseDate"));
        Put(input, "terminationDate", Date(t, "terminationDate"));
        Put(input, "priceAtTerminationDate", Num(t, "priceAtTerminationDate"));
        return input;
    }

    // ── Run and compare ──────────────────────────────────────────────────────────

    private static (JArray? Events, string? Error) Run(CompiledScript<JToken> script, JObject input)
    {
        try
        {
            var context = JsonExecutionContext.CreateDefault();
            var result = script.Execute(input, context);
            if (!result.Success)
            {
                var reasons = string.Join("; ", context.GetLogEntries()
                    .Where(e => e.Level.ToString() == "Error").Take(2).Select(e => $"{e.Level}: {e.Message}"));
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

    private enum Mode
    {
        /// <summary>All six fields, every event.</summary>
        Strict,
        /// <summary>Date, type and payoff of every event (what pam-simple emits).</summary>
        DateTypePayoff,
        /// <summary>Date, type and payoff of the events that move money; zero-payoff events (IP at IED, RR, IPCI) are ignored on both sides.</summary>
        CashFlows,
    }

    /// <summary>Returns the differences (empty = the case passes).</summary>
    private static List<string> Compare(string caseId, JObject testCase, JArray? actual, Mode mode)
    {
        var diffs = new List<string>();
        if (actual is null) { diffs.Add("no events"); return diffs; }

        var expected = (JArray)testCase["results"]!;
        if (mode == Mode.CashFlows)
        {
            static bool Moves(JToken e, string field) => System.Math.Abs(Round10(e[field]?.Value<double>() ?? 0)) > 0;
            expected = new JArray(expected.Where(e => Moves(e, "payoff")));
            actual = new JArray(actual.Where(e => Moves(e, "payoff")));
        }
        if (actual.Count != expected.Count)
        {
            diffs.Add($"event count {actual.Count} != expected {expected.Count}");
            return diffs;
        }

        void CheckNum(int i, string field, JToken exp, JToken act)
        {
            if (act is null || act.Type == JTokenType.Null) { diffs.Add($"[{i}] {field}: not emitted"); return; }
            var e = Round10(exp.Value<double>());
            var a = Round10(act.Value<double>());
            if (System.Math.Abs(e - a) > Tolerance)
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

            CheckNum(i, "payoff", exp["payoff"]!, act["payoff"]!);
            if (mode != Mode.Strict) continue;

            CheckNum(i, "notionalPrincipal", exp["notionalPrincipal"]!, act["notionalPrincipal"]!);
            CheckNum(i, "accruedInterest", exp["accruedInterest"]!, act["accruedInterest"]!);
            if (!RateColumnInconsistent.Contains(caseId))
                CheckNum(i, "nominalInterestRate", exp["nominalInterestRate"]!, act["nominalInterestRate"]!);
        }
        return diffs;
    }

    private static string? First(List<string> diffs) => diffs.Count == 0 ? null : diffs[0];

    // ── Tests ────────────────────────────────────────────────────────────────────

    private static readonly Lazy<CompiledScript<JToken>> SimpleScript = new(() => Compile("pam-simple.json"));
    private static readonly Lazy<CompiledScript<JToken>> ReferenceScript = new(() => Compile("pam-reference.json"));

    /// <summary>
    /// Baseline: pam-simple.json as it ships, against every reference case. Asserts nothing about
    /// the reference set except that the recorded pass sets still hold, so the baseline cannot drift.
    /// </summary>
    [TestCaseSource(nameof(CaseIds))]
    public void PamSimple_Baseline(string caseId)
    {
        var testCase = (JObject)ReferenceCases.Value[caseId]!;
        var (events, error) = Run(SimpleScript.Value, BuildInput(testCase));

        string? Diff(Mode mode) => error ?? First(Compare(caseId, testCase, events, mode));
        var strict = Diff(Mode.Strict);
        var dtp = Diff(Mode.DateTypePayoff);
        var cash = Diff(Mode.CashFlows);
        var row = Rows.GetOrAdd(caseId, _ => new Row());
        row.BaselineRan = true;
        row.BaselineStrict = strict;
        row.BaselineDtp = dtp;
        row.BaselineCash = cash;

        Assert.That(strict is null, Is.EqualTo(BaselineStrictPasses.Contains(caseId)),
            $"{caseId}: strict baseline result changed ({strict ?? "now passes"}); update {nameof(BaselineStrictPasses)}.");
        Assert.That(dtp is null, Is.EqualTo(BaselineDtpPasses.Contains(caseId)),
            $"{caseId}: date/type/payoff baseline result changed ({dtp ?? "now passes"}); update {nameof(BaselineDtpPasses)}.");
        Assert.That(cash is null, Is.EqualTo(BaselineCashPasses.Contains(caseId)),
            $"{caseId}: cash-flow baseline result changed ({cash ?? "now passes"}); update {nameof(BaselineCashPasses)}.");
    }

    /// <summary>pam-reference.json against every reference case; known gaps are listed, not hidden.</summary>
    [TestCaseSource(nameof(CaseIds))]
    public void PamReference_MatchesActusReferenceCases(string caseId)
    {
        var testCase = (JObject)ReferenceCases.Value[caseId]!;
        var (events, error) = Run(ReferenceScript.Value, BuildInput(testCase));

        var diffs = error is null ? Compare(caseId, testCase, events, Mode.Strict) : [error];
        var row = Rows.GetOrAdd(caseId, _ => new Row());
        row.ReferenceRan = true;
        row.Reference = First(diffs);

        if (KnownFailures.TryGetValue(caseId, out var reason))
        {
            Assert.That(diffs, Is.Not.Empty,
                $"{caseId} now passes: remove it from {nameof(KnownFailures)} (was: {reason}).");
            Assert.Warn($"{caseId} known failure: {reason} | first difference: {diffs[0]}");
        }
        else
        {
            Assert.That(diffs, Is.Empty, $"{caseId}: {string.Join(" | ", diffs.Take(5))}");
        }
    }

    private static string Trim(string s) => s.Length <= 70 ? s : s[..70] + "...";

    [OneTimeTearDown]
    public void PrintTable()
    {
        if (Rows.IsEmpty) return;
        var ids = Rows.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        string Mark(bool ran, string? d) => !ran ? "-" : d is null ? "pass" : "FAIL";
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("ACTUS PAM reference cases against Tlio, to 10 decimals");
        sb.AppendLine("  strict  = every event: date, type, payoff, notional, nominal rate, accrued interest");
        sb.AppendLine("  dtp     = every event: date, type, payoff (all pam-simple emits)");
        sb.AppendLine("  cash    = only events that move money (zero-payoff IP at IED, RR, IPCI ignored): date, type, payoff");
        sb.AppendLine("case   | simple strict | simple dtp | simple cash | reference | first difference (pam-simple strict | pam-simple cash | pam-reference)");
        sb.AppendLine("-------|---------------|------------|-------------|-----------|------------------------------------------------------------------");
        foreach (var id in ids)
        {
            var r = Rows[id];
            sb.AppendLine($"{id,-6} | {Mark(r.BaselineRan, r.BaselineStrict),-13} | {Mark(r.BaselineRan, r.BaselineDtp),-10} | " +
                          $"{Mark(r.BaselineRan, r.BaselineCash),-11} | {Mark(r.ReferenceRan, r.Reference),-9} | " +
                          $"{(r.BaselineStrict is null ? "-" : Trim(r.BaselineStrict))} | {(r.BaselineCash is null ? "-" : Trim(r.BaselineCash))} | {(r.Reference is null ? "-" : Trim(r.Reference))}");
        }
        int Count(Func<Row, bool> ran, Func<Row, string?> d) => ids.Count(id => ran(Rows[id]) && d(Rows[id]) is null);
        sb.AppendLine($"passing of {ids.Count}: pam-simple strict {Count(r => r.BaselineRan, r => r.BaselineStrict)}, dtp {Count(r => r.BaselineRan, r => r.BaselineDtp)}, " +
                      $"cash {Count(r => r.BaselineRan, r => r.BaselineCash)}; pam-reference {Count(r => r.ReferenceRan, r => r.Reference)}");
        TestContext.Progress.WriteLine(sb.ToString());
    }
}
