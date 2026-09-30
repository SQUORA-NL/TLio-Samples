using System.Globalization;
using System.Text.Json;
using ActusInsurance.Core.CPU.Contracts;
using ActusInsurance.Core.Externals;
using ActusInsurance.Core.Models;

namespace ActusOracle;

using Terms = Dictionary<string, string>;
using Observed = Dictionary<string, List<(DateTime T, double V)>>;

public static class Pam
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── Run one contract through ACTUS-I's CPU PAM, exactly as ActusInsurance.Tests.CPU does ──
    public static Dictionary<string, object?> Run(string id, string source, Terms terms, Observed observed)
    {
        var rec = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["source"] = source,
            ["terms"] = terms,
            ["dataObserved"] = observed.ToDictionary(
                kv => kv.Key,
                kv => (object?)kv.Value.Select(p => new Dictionary<string, object?> { ["timestamp"] = J.Iso(p.T), ["value"] = p.V }).ToList()),
        };

        List<Dictionary<string, object?>>? events = null;
        string? error = null;
        var th = new Thread(() =>
        {
            try { events = Compute(terms, observed); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        }) { IsBackground = true };
        th.Start();
        if (!th.Join(TimeSpan.FromSeconds(20))) error = "Timeout: ACTUS-I did not return within 20 s";

        if (error is not null) rec["error"] = error;
        else if (events is null || events.Count == 0) rec["error"] = "ACTUS-I generated no events";
        else rec["events"] = events;
        return rec;
    }

    private static List<Dictionary<string, object?>> Compute(Terms terms, Observed observed)
    {
        var dict = terms.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        var model = PamContractTerms.FromDictionary(dict);

        var rf = new RiskFactorModel();
        foreach (var (code, pts) in observed)
            foreach (var (t, v) in pts)
                rf.AddRate(code, t, v);

        var schedule = PrincipalAtMaturity.Schedule(model.MaturityDate, model);
        if (schedule.Count == 0) return new();
        schedule = PrincipalAtMaturity.Apply(schedule, model, rf);

        return schedule.Select(e => new Dictionary<string, object?>
        {
            ["eventDate"] = J.Iso(e.Time),
            ["scheduleDate"] = J.Iso(e.ScheduleTime),
            ["eventType"] = e.Type.ToString(),
            ["payoff"] = e.Payoff,
            ["notionalPrincipal"] = e.NotionalPrincipal,
            ["nominalInterestRate"] = e.NominalInterestRate,
            ["accruedInterest"] = e.AccruedInterest,
            ["feeAccrued"] = e.FeeAccrued,
            ["currency"] = e.Currency,
        }).ToList();
    }

    // ── (a) the 42 reference cases ───────────────────────────────────────────────
    public static List<object?> ReferenceCases(string jsonPath, out int matching)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var list = new List<object?>();
        matching = 0;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var tc = prop.Value;
            var terms = new Terms();
            foreach (var t in tc.GetProperty("terms").EnumerateObject())
                terms[t.Name] = t.Value.ValueKind == JsonValueKind.String ? t.Value.GetString()! : t.Value.GetRawText();

            var observed = new Observed();
            if (tc.TryGetProperty("dataObserved", out var dobs) && dobs.ValueKind == JsonValueKind.Object)
                foreach (var ds in dobs.EnumerateObject())
                {
                    var pts = new List<(DateTime, double)>();
                    foreach (var p in ds.Value.GetProperty("data").EnumerateArray())
                        pts.Add((DateTime.Parse(p.GetProperty("timestamp").GetString()!, Inv), double.Parse(p.GetProperty("value").GetString()!, Inv)));
                    observed[ds.Value.TryGetProperty("identifier", out var idp) ? idp.GetString()! : ds.Name] = pts;
                }

            var rec = Run(prop.Name, "actus-tests-pam.json", terms, observed);

            // Does ACTUS-I's own CPU output equal the published reference to 10 decimals?
            var expected = tc.GetProperty("results").EnumerateArray().ToList();
            rec["referenceEventCount"] = expected.Count;
            if (rec.TryGetValue("events", out var evs) && evs is List<Dictionary<string, object?>> got)
            {
                var diff = CompareToReference(expected, got);
                rec["matchesReference"] = diff is null;
                if (diff is not null) rec["firstReferenceDifference"] = diff;
                if (diff is null) matching++;
            }
            else rec["matchesReference"] = false;
            list.Add(rec);
        }
        return list;
    }

    private static double R10(double d) => Math.Round(d, 10);

    private static string? CompareToReference(List<JsonElement> expected, List<Dictionary<string, object?>> got)
    {
        // Same rule as ActusInsurance.Tests.CPU: computed count >= expected count, compare by index,
        // rounded to 10 decimals, nominalInterestRate not compared (known inconsistency in the reference file).
        if (got.Count < expected.Count) return $"computed {got.Count} events < reference {expected.Count}";
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var g = got[i];
            if (!DateTime.TryParse(e.GetProperty("eventDate").GetString()!, Inv, DateTimeStyles.None, out var ed) || J.Iso(ed) != (string)g["eventDate"]!)
                return $"[{i}] eventDate reference={e.GetProperty("eventDate").GetString()} computed={g["eventDate"]}";
            if (!string.Equals(e.GetProperty("eventType").GetString(), (string)g["eventType"]!, StringComparison.OrdinalIgnoreCase))
                return $"[{i}] eventType reference={e.GetProperty("eventType").GetString()} computed={g["eventType"]}";
            foreach (var f in new[] { "payoff", "notionalPrincipal", "accruedInterest", "feeAccrued" })
                if (e.TryGetProperty(f, out var ev) && ev.ValueKind == JsonValueKind.Number)
                {
                    var exp = R10(ev.GetDouble());
                    var act = R10((double)g[f]!);
                    if (Math.Abs(exp - act) > 2e-10) return $"[{i}] {f} reference={J.Inv(exp)} computed={J.Inv(act)}";
                }
        }
        return null;
    }

    // ── (b) seeded generator ─────────────────────────────────────────────────────
    private static readonly string[] DayCounts = { "AA", "A360", "A365", "30E360ISDA", "30E360", "B252", "A336" };
    private static readonly string[] Bdcs = { "NOS", "CSF", "CSMF", "CSP", "CSMP", "SCF", "SCMF", "SCP", "SCMP" };
    private static readonly string[] Calendars = { "NC", "MF", "MFH" };
    private static readonly string[] Roles = { "RPA", "RPL" };
    private static readonly string[] OtherRoles = { "BUY", "SEL", "RFL", "PFL", "RF", "PF" };

    private static string D(DateTime d) => J.Iso(d);
    private static string N(double d) => J.Inv(d);

    private static string Cycle(Rng r, bool shortTenor)
    {
        // ACTUS-I's cycle grammar (CycleUtils.ParsePeriod): ISO P{n}Y{n}M{n}D with optional L0/L1 stub,
        // or a bare {n}{D|M|Q|H|Y}. P..Q / P..H / P..W are NOT accepted, so quarterly is P3M, weekly P7D.
        var roll = r.Int(0, 99);
        if (roll < 1) return r.Pick(new[] { "P1QL0", "P1HL0", "P2WL0" });   // unsupported on purpose: recorded as errors
        if (roll < 5) return r.Pick(new[] { "1M", "3M", "6M", "1Y" });       // bare custom form (no stub letter)
        if (roll < 8) return r.Pick(new[] { "P1Y6ML0", "P1M15DL1", "P0Y0M45DL0" });  // compound ISO periods
        var (n, unit) = r.Pick(shortTenor
            ? new[] { (7, "D"), (14, "D"), (27, "D"), (30, "D"), (1, "M"), (1, "M"), (1, "M"), (2, "M"), (3, "M"), (6, "M") }
            : new[] { (1, "M"), (2, "M"), (3, "M"), (3, "M"), (4, "M"), (6, "M"), (6, "M"), (1, "Y"), (1, "Y"), (30, "D"), (7, "D") });
        return $"P{n}{unit}L{r.Int(0, 1)}";
    }

    public static List<object?> Generated(int count, ulong seed)
    {
        var r = new Rng(seed);
        var list = new List<object?>();
        for (var i = 0; i < count; i++)
        {
            var terms = new Terms();
            var observed = new Observed();

            // Tenor keeps event counts modest (the file must stay small): mostly 1-6 years.
            var tenorYears = r.Pick(new[] { 1, 1, 2, 2, 3, 3, 4, 5, 6, 8, 10 });
            var extraDays = r.Chance(0.3) ? r.Int(1, 60) : 0;
            var year = r.Int(2010, 2030);
            var month = r.Int(1, 12);
            var day = r.Chance(0.35) ? DateTime.DaysInMonth(year, month) : r.Int(1, 28); // month ends matter
            var ied = new DateTime(year, month, day);
            var md = ied.AddYears(tenorYears).AddDays(extraDays);
            var shortTenor = tenorYears <= 2;

            terms["contractType"] = "PAM";
            terms["contractID"] = $"gen{i:D4}";
            terms["currency"] = "USD";
            terms["contractRole"] = r.Chance(0.03) ? r.Pick(OtherRoles) : r.Pick(Roles);
            var statusBefore = r.Chance(0.85);
            var status = statusBefore ? ied.AddDays(-r.Int(1, 3)) : ied.AddDays((int)((md - ied).TotalDays * r.Int(10, 60) / 100.0));
            terms["statusDate"] = D(status);
            terms["contractDealDate"] = D(status.AddDays(-2));
            terms["initialExchangeDate"] = D(ied);
            terms["maturityDate"] = D(md);
            var notional = r.Pick(new[] { 1000, 3000, 10000, 25000, 100000, 500000, 1000000 });
            terms["notionalPrincipal"] = N(notional);
            var rate = r.Chance(0.06) ? 0.0 : r.Chance(0.04) ? -r.Int(1, 5) / 1000.0 : r.Int(5, 120) / 1000.0;
            terms["nominalInterestRate"] = N(rate);
            terms["dayCountConvention"] = r.Pick(DayCounts);
            terms["endOfMonthConvention"] = r.Chance(0.35) ? "EOM" : "SD";

            // Business days: mostly none.
            if (r.Chance(0.3))
            {
                terms["businessDayConvention"] = r.Pick(Bdcs);
                terms["calendar"] = r.Pick(Calendars);
            }
            else if (r.Chance(0.1)) terms["calendar"] = r.Pick(Calendars);

            // Interest payment cycle: none (only maturity), or a cycle with anchor variants.
            var hasIp = r.Chance(0.9);
            if (hasIp)
            {
                terms["cycleOfInterestPayment"] = Cycle(r, shortTenor);
                var a = r.Int(0, 3);
                if (a == 1) terms["cycleAnchorDateOfInterestPayment"] = D(ied);
                else if (a == 2) terms["cycleAnchorDateOfInterestPayment"] = D(ied.AddDays(r.Int(1, 45)));
                else if (a == 3) terms["cycleAnchorDateOfInterestPayment"] = D(ied.AddMonths(-r.Int(1, 5)));
                // a == 0: anchor omitted -> ACTUS-I defaults to IED
            }
            if (r.Chance(0.4)) terms["premiumDiscountAtIED"] = N(Math.Round(notional * r.Int(-50, 50) / 1000.0, 2));
            if (r.Chance(0.15)) terms["rateMultiplier"] = N(r.Pick(new[] { 0.5, 1.5, 2.0, 2.5, 3.0 }));
            else if (r.Chance(0.6)) terms["rateMultiplier"] = "1.0";

            // Rate reset
            var hasRr = r.Chance(0.35);
            if (hasRr)
            {
                terms["cycleOfRateReset"] = Cycle(r, shortTenor);
                if (r.Chance(0.6)) terms["cycleAnchorDateOfRateReset"] = D(ied.AddMonths(r.Int(1, 4)));
                terms["rateSpread"] = N(r.Int(-10, 30) / 1000.0);
                terms["marketObjectCodeOfRateReset"] = "RATE_A";
                var pts = new List<(DateTime, double)>();
                var t = ied.AddMonths(-3);
                var v = 0.01 + r.NextDouble() * 0.04;
                while (t < md.AddMonths(6))
                {
                    v = Math.Max(0.0005, v + (r.NextDouble() - 0.5) * 0.006);
                    pts.Add((t, Math.Round(v, 10)));
                    t = t.AddMonths(2).AddDays(r.Int(-5, 5));
                }
                observed["RATE_A"] = pts;
                if (r.Chance(0.25))
                {
                    var lifeFloor = r.Int(0, 20) / 1000.0;
                    terms["lifeFloor"] = N(lifeFloor);
                    terms["lifeCap"] = N(lifeFloor + r.Int(10, 60) / 1000.0);
                }
                if (r.Chance(0.2))
                {
                    terms["periodFloor"] = N(-r.Int(1, 10) / 1000.0);
                    terms["periodCap"] = N(r.Int(1, 15) / 1000.0);
                }
                if (r.Chance(0.15)) terms["nextResetRate"] = N(r.Int(5, 80) / 1000.0);
                if (r.Chance(0.1)) terms["fixingPeriod"] = "P2D";
            }

            // Fees
            if (r.Chance(0.25))
            {
                terms["cycleOfFee"] = Cycle(r, shortTenor);
                if (r.Chance(0.5)) terms["cycleAnchorDateOfFee"] = D(ied.AddMonths(r.Int(1, 6)));
                var basis = r.Chance(0.5) ? "A" : "N";
                terms["feeBasis"] = basis;
                terms["feeRate"] = basis == "A" ? N(r.Int(5, 50)) : N(r.Int(1, 15) / 1000.0);
                if (r.Chance(0.3)) terms["feeAccrued"] = N(r.Int(0, 30));
            }

            // Purchase / termination
            var span = (md - ied).TotalDays;
            if (r.Chance(0.12))
            {
                terms["purchaseDate"] = D(ied.AddDays((int)(span * r.Int(15, 45) / 100.0)));
                terms["priceAtPurchaseDate"] = N(Math.Round(notional * (0.95 + r.NextDouble() * 0.1), 2));
            }
            if (r.Chance(0.12))
            {
                terms["terminationDate"] = D(ied.AddDays((int)(span * r.Int(50, 90) / 100.0)));
                terms["priceAtTerminationDate"] = N(Math.Round(notional * (0.95 + r.NextDouble() * 0.1), 2));
            }

            // Capitalization, initial accrued interest
            if (r.Chance(0.1)) terms["capitalizationEndDate"] = D(ied.AddDays((int)(span * r.Int(20, 70) / 100.0)));
            if (r.Chance(0.08)) terms["accruedInterest"] = N(Math.Round(notional * rate * r.Int(1, 90) / 365.0, 2));

            // Scaling
            if (r.Chance(0.08))
            {
                terms["marketObjectCodeOfScalingIndex"] = "CPI";
                terms["scalingIndexAtContractDealDate"] = "100";
                terms["scalingEffect"] = r.Pick(new[] { "I00", "0N0", "IN0" });
                terms["cycleOfScalingIndex"] = r.Pick(new[] { "P3ML0", "P6ML0", "P1YL0" });
                if (r.Chance(0.5)) terms["cycleAnchorDateOfScalingIndex"] = D(ied.AddMonths(r.Int(1, 6)));
                if (r.Chance(0.3)) terms["notionalScalingMultiplier"] = N(r.Pick(new[] { 0.9, 1.1 }));
                if (r.Chance(0.3)) terms["interestScalingMultiplier"] = N(r.Pick(new[] { 0.9, 1.1 }));
                var pts = new List<(DateTime, double)>();
                var t = ied.AddMonths(-3);
                var v = 100.0;
                while (t < md.AddMonths(6))
                {
                    v += r.NextDouble() * 1.2 - 0.2;
                    pts.Add((t, Math.Round(v, 6)));
                    t = t.AddMonths(3);
                }
                observed["CPI"] = pts;
            }

            if (r.Chance(0.03)) terms["contractPerformance"] = r.Pick(new[] { "PF", "DL", "DQ", "DF" });

            list.Add(Run(terms["contractID"], "generated (seed " + seed + ")", terms, observed));
        }
        return list;
    }

    /// <summary>All terms ACTUS-I's FromDictionary reads, and whether the generator varies them.</summary>
    public static readonly (string Term, bool Varied, string Note)[] Coverage =
    {
        ("contractType", false, "constant PAM (not read by FromDictionary)"),
        ("contractID", true, "unique per case"),
        ("currency", false, "constant USD; no FX rate data supplied, so FX is 1"),
        ("contractRole", true, "RPA/RPL mostly; ~3% BUY/SEL/RFL/PFL/RF/PF"),
        ("statusDate", true, "1-3 days before IED (85%), or 10-60% into the life (15%)"),
        ("contractDealDate", false, "written but ignored by FromDictionary"),
        ("initialExchangeDate", true, "2010-2030, ~35% month ends"),
        ("maturityDate", true, "1-10 years after IED plus 0-60 extra days"),
        ("purchaseDate", true, "12%"),
        ("priceAtPurchaseDate", true, "with purchaseDate"),
        ("terminationDate", true, "12%"),
        ("priceAtTerminationDate", true, "with terminationDate"),
        ("capitalizationEndDate", true, "10%"),
        ("notionalPrincipal", true, "1,000 to 1,000,000"),
        ("nominalInterestRate", true, "0 (6%), negative (4%), 0.5% to 12%"),
        ("accruedInterest", true, "8%"),
        ("premiumDiscountAtIED", true, "40%, +-5% of notional"),
        ("rateSpread", true, "with rate reset"),
        ("rateMultiplier", true, "1.0, or 0.5 to 3.0 (15%)"),
        ("nextResetRate", true, "15% of rate-reset cases (produces an RRF event)"),
        ("marketObjectCodeOfRateReset", true, "RATE_A with random-walk observations every ~2 months (closest previous value is used)"),
        ("fixingPeriod", true, "10% of rate-reset cases, P2D (stored only, never used by the CPU engine)"),
        ("lifeCap / lifeFloor", true, "25% of rate-reset cases"),
        ("periodCap / periodFloor", true, "20% of rate-reset cases"),
        ("cycleOfInterestPayment", true, "ISO P{n}D/M/Y with L0/L1 (compound periods too), bare {n}M/Y, ~2% deliberately unsupported (P1Q, P1H, P2W: ACTUS-I rejects them), or absent (10%)"),
        ("cycleAnchorDateOfInterestPayment", true, "absent (defaults to IED), IED, IED+1..45 days, IED-1..5 months"),
        ("cyclePointOfInterestPayment", false, "stored only, never used by the CPU engine"),
        ("cycleOfRateReset", true, "35% of cases"),
        ("cycleAnchorDateOfRateReset", true, "absent or IED+1..4 months"),
        ("cyclePointOfRateReset", false, "stored only, never used by the CPU engine"),
        ("cycleOfFee", true, "25%"),
        ("cycleAnchorDateOfFee", true, "absent or IED+1..6 months"),
        ("feeBasis", true, "A or N"),
        ("feeRate", true, "with fees"),
        ("feeAccrued", true, "30% of fee cases"),
        ("marketObjectCodeOfScalingIndex", true, "CPI, 8%"),
        ("scalingIndexAtContractDealDate", false, "constant 100 in scaling cases"),
        ("notionalScalingMultiplier", true, "30% of scaling cases"),
        ("interestScalingMultiplier", true, "30% of scaling cases"),
        ("cycleOfScalingIndex", true, "P3ML0, P6ML0, P1YL0"),
        ("cycleAnchorDateOfScalingIndex", true, "absent or IED+1..6 months"),
        ("scalingEffect", true, "I00, 0N0, IN0"),
        ("dayCountConvention", true, "all 7: AA, A360, A365, 30E360ISDA, 30E360, B252, A336"),
        ("businessDayConvention", true, "all 9 with calendar NC/MF/MFH (30% of cases); NOS otherwise"),
        ("endOfMonthConvention", true, "SD / EOM"),
        ("calendar", true, "NC, MF, MFH (MFH is built with an EMPTY holiday set in ActusInsurance.Core.CPU, so it behaves like MF)"),
        ("contractPerformance", true, "3%; stored in the state space only, no effect on events"),
    };
}
