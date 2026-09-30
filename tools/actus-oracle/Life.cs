using System.Text.Json;
using System.Text.RegularExpressions;
using ActusInsurance.LifeInsurance.GPU;
using ActusInsurance.LifeInsurance.GPU.Dsl;
using ActusInsurance.LifeInsurance.GPU.Kernels;
using ActusInsurance.LifeInsurance.GPU.Models;
using ActusInsurance.LifeInsurance.GPU.RulePack;
using ActusInsurance.LifeInsurance.GPU.Sinks;

namespace ActusOracle;

using Rec = Dictionary<string, object?>;

public static class Life
{
    public static int Days(int y, int m, int d) => new DateOnly(y, m, d).DayNumber - new DateOnly(1970, 1, 1).DayNumber;
    public static readonly int EvalDate = Days(2025, 1, 1);

    // ── Contracts ───────────────────────────────────────────────────────────────

    /// <summary>Copy of ActusInsurance.Benchmarks.LifeInsuranceBenchmarkData.BuildBaseContracts (42 contracts).</summary>
    public static List<(string Name, LifeContractGpu C)> Benchmark42()
    {
        var list = new List<(string, LifeContractGpu)>(42);
        ulong idSeq = 1;
        var ages = new[] { 25f, 35f, 40f, 45f, 50f, 55f, 60f, 65f };
        var genders = new[] { InsuredGender.M, InsuredGender.F, InsuredGender.X };
        var smokers = new[] { false, true };
        var index = 0;
        foreach (var age in ages)
            foreach (var gender in genders)
                foreach (var smoker in smokers)
                {
                    if (++index > 42) break;
                    double sumAssured = (index % 4) switch { 0 => 50_000, 1 => 100_000, 2 => 200_000, _ => 500_000 };
                    double premiumAmount = sumAssured * 0.001;
                    uint premiumMode = (uint)(index % 3);
                    int extraBps = (index % 4) switch { 0 => 0, 1 => 100, 2 => 200, _ => 500 };
                    float yif = (index % 4) switch { 0 => 0.5f, 1 => 2.0f, 2 => 5.0f, _ => 10.0f };
                    list.Add(($"bench{index:D2}", new LifeContractGpu
                    {
                        ContractIdHi = 0xBEEF0000_00000000UL | idSeq,
                        ContractIdLo = idSeq++,
                        CurrentState = (uint)LifeState.Active,
                        SmokerStatus = smoker ? 1u : 0u,
                        InsuredGender = (uint)gender,
                        PremiumMode = premiumMode,
                        AgeAtEval = age,
                        SumAssured = sumAssured,
                        PremiumAmount = premiumAmount,
                        YearsInForce = yif,
                        ExtraPremBps = extraBps,
                        LapseDateDays = -1,
                        GraceExpiryDays = -1,
                        LastPremDueDays = Days(2025, 1, 1),
                    }));
                }
        return list;
    }

    private static LifeContractGpu Mk(uint state, uint smoker, InsuredGender g, PremiumMode pm, float age, double sa,
        double prem, float yif, int bps, ulong id) => new()
        {
            ContractIdHi = 0xED6E0000_00000000UL | id,
            ContractIdLo = id,
            CurrentState = state,
            SmokerStatus = smoker,
            InsuredGender = (uint)g,
            PremiumMode = (uint)pm,
            AgeAtEval = age,
            SumAssured = sa,
            PremiumAmount = prem,
            YearsInForce = yif,
            ExtraPremBps = bps,
            LapseDateDays = -1,
            GraceExpiryDays = -1,
            LastPremDueDays = Days(2025, 1, 1),
        };

    public static List<(string Name, LifeContractGpu C)> Edge()
    {
        var a = (uint)LifeState.Active;
        return new()
        {
            ("edge01_age20_min", Mk(a, 0, InsuredGender.M, PremiumMode.Monthly, 20f, 100_000, 100, 0f, 0, 1)),
            ("edge02_age80_max", Mk(a, 0, InsuredGender.F, PremiumMode.Annual, 80f, 100_000, 1200, 5f, 0, 2)),
            ("edge03_age19_5_below_table", Mk(a, 0, InsuredGender.M, PremiumMode.Monthly, 19.5f, 100_000, 100, 1f, 0, 3)),
            ("edge04_age85_above_table", Mk(a, 1, InsuredGender.M, PremiumMode.Quarterly, 85f, 100_000, 300, 20f, 0, 4)),
            ("edge05_age100", Mk(a, 0, InsuredGender.X, PremiumMode.Annual, 100f, 50_000, 500, 30f, 0, 5)),
            ("edge06_X_smoker_bps5000", Mk(a, 1, InsuredGender.X, PremiumMode.Monthly, 45f, 250_000, 250, 2f, 5000, 6)),
            ("edge07_bps_minus5000", Mk(a, 0, InsuredGender.M, PremiumMode.Monthly, 45f, 250_000, 250, 2f, -5000, 7)),
            ("edge08_bps10000_beyond_clamp", Mk(a, 1, InsuredGender.M, PremiumMode.Monthly, 70f, 250_000, 250, 2f, 10_000, 8)),
            ("edge09_yif30_table_max", Mk(a, 0, InsuredGender.F, PremiumMode.Monthly, 30f, 100_000, 100, 30f, 0, 9)),
            ("edge10_yif35_above_table", Mk(a, 0, InsuredGender.F, PremiumMode.Quarterly, 30f, 100_000, 300, 35f, 0, 10)),
            ("edge11_yif0_annual", Mk(a, 0, InsuredGender.M, PremiumMode.Annual, 33.3f, 100_000, 1200, 0f, 0, 11)),
            ("edge12_yif0_49_bracket", Mk(a, 0, InsuredGender.M, PremiumMode.Monthly, 52.75f, 100_000, 100, 0.49f, 200, 12)),
            ("edge13_F_smoker_age79_9", Mk(a, 1, InsuredGender.F, PremiumMode.Quarterly, 79.9f, 300_000, 900, 3f, 100, 13)),
            ("edge14_terminated", Mk((uint)LifeState.Terminated, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 100_000, 100, 3f, 0, 14)),
            ("edge15_claim_paid", Mk((uint)LifeState.ClaimPaid, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 100_000, 100, 3f, 0, 15)),
            ("edge16_death_claim_paid", Mk((uint)LifeState.DeathClaimPaid, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 100_000, 100, 3f, 0, 16)),
            ("edge17_grace_period", Mk((uint)LifeState.GracePeriod, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 100_000, 100, 3f, 0, 17)),
            ("edge18_paid_up", Mk((uint)LifeState.PaidUp, 0, InsuredGender.F, PremiumMode.Monthly, 55f, 100_000, 100, 8f, 0, 18)),
            ("edge19_claim_open", Mk((uint)LifeState.ClaimOpen, 1, InsuredGender.M, PremiumMode.Monthly, 60f, 100_000, 100, 8f, 0, 19)),
            ("edge20_lapsed", Mk((uint)LifeState.Lapsed, 0, InsuredGender.M, PremiumMode.Monthly, 41f, 100_000, 100, 4f, 0, 20)),
            ("edge21_reinstatement", Mk((uint)LifeState.Reinstatement, 0, InsuredGender.F, PremiumMode.Quarterly, 41f, 100_000, 300, 4f, 0, 21)),
            ("edge22_prospect", Mk((uint)LifeState.Prospect, 0, InsuredGender.M, PremiumMode.Monthly, 41f, 100_000, 100, 0f, 0, 22)),
        };
    }

    public static Rec ContractRec(string name, in LifeContractGpu c) => new()
    {
        ["name"] = name,
        ["currentState"] = c.CurrentState,
        ["currentStateName"] = ((LifeState)c.CurrentState).ToString(),
        ["smokerStatus"] = c.SmokerStatus,
        ["insuredGender"] = c.InsuredGender,
        ["premiumMode"] = c.PremiumMode,
        ["ageAtEval"] = (double)c.AgeAtEval,        // float32 widened, exact
        ["sumAssured"] = c.SumAssured,
        ["premiumAmount"] = c.PremiumAmount,
        ["yearsInForce"] = (double)c.YearsInForce,  // float32 widened, exact
        ["claimsCount"] = c.ClaimsCount,
        ["lapseCount"] = c.LapseCount,
        ["extraPremBps"] = c.ExtraPremBps,
        ["lapseDateDays"] = c.LapseDateDays,
        ["graceExpiryDays"] = c.GraceExpiryDays,
        ["lastPremDueDays"] = c.LastPremDueDays,
    };

    // ── (a) tables ──────────────────────────────────────────────────────────────

    public static Rec Tables(string libDir, RefKernels.Tables tb)
    {
        static double[] W(float[] f) => f.Select(x => (double)x).ToArray();
        static object? Parse(string p) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(p));
        return new Rec
        {
            ["kind"] = "tables",
            ["description"] = "Tables exactly as LifeGpuParameterBuilder builds them (float32, widened to double here without change). Row-major, 3 columns: mortality/disability = age row x gender (M,F,X); lapse = duration row x premium mode (Monthly,Quarterly,Annual).",
            ["constants"] = new Rec
            {
                ["mortalityAgeMin"] = (double)LifeGpuParameterBuilder.MortalityAgeMin,
                ["mortalityAgeStep"] = (double)LifeGpuParameterBuilder.MortalityAgeStep,
                ["mortalityAgeCount"] = LifeGpuParameterBuilder.MortalityAgeCount,
                ["lapseDurMin"] = (double)LifeGpuParameterBuilder.LapseDurMin,
                ["lapseDurStep"] = (double)LifeGpuParameterBuilder.LapseDurStep,
                ["lapseDurCount"] = LifeGpuParameterBuilder.LapseDurCount,
                ["stride"] = 3,
                ["hazardCodes"] = new Rec { ["none"] = 0, ["mortality"] = 1, ["lapse"] = 2, ["disability"] = 3 },
                ["smokerLoading"] = 1.35,
                ["qxCap"] = 0.999,
                ["disabilityCap"] = 0.05,
                ["premiumModeFactor"] = new[] { 12.0, 4.0, 1.0 },
                ["terminalStates"] = new[] { 6, 7, 9 },
                ["stateNames"] = Enum.GetNames<LifeState>(),
            },
            ["mortality"] = W(tb.Mortality),
            ["lapse"] = W(tb.Lapse),
            ["disability"] = W(tb.Disability),
            ["mortalityDoublePrecision"] = tb.MortalityD,
            ["lapseDoublePrecision"] = tb.LapseD,
            ["disabilityDoublePrecision"] = tb.DisabilityD,
            ["markovHazards10x10"] = tb.Hazards,
            ["markovGraph"] = Parse(Path.Combine(libDir, "RulePack", "markov_graph.json")),
            ["factorSet"] = Parse(Path.Combine(libDir, "RulePack", "factor_set.json")),
            ["ruleSet"] = Parse(Path.Combine(libDir, "RulePack", "rule_set.json")),
        };
    }

    // ── (b) factors ─────────────────────────────────────────────────────────────

    public static Rec Factors(List<(string Name, LifeContractGpu C)> all)
    {
        var items = new List<object?>();
        foreach (var (name, c) in all)
        {
            var dob = EvalDate - (int)Math.Round(c.AgeAtEval * 365.25);
            var f = LifeFactorEvaluator.Evaluate(c, EvalDate, dob);
            var fr = new Rec();
            foreach (var p in f.GetType().GetProperties()) fr[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(f);
            foreach (var p in f.GetType().GetFields()) fr[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(f);
            items.Add(new Rec { ["contract"] = ContractRec(name, c), ["evalDateDays"] = EvalDate, ["insuredDobDays"] = dob, ["factors"] = fr });
        }
        return new Rec
        {
            ["kind"] = "factors",
            ["description"] = "LifeFactorEvaluator.Evaluate (CPU reference for the 8 factors). dob = evalDate - round(ageAtEval*365.25), so factorAge is within 0.5 day of ageAtEval.",
            ["items"] = items,
        };
    }

    // ── (c) projection ──────────────────────────────────────────────────────────

    public static readonly (int Steps, double Dt, string Label)[] Configs =
    {
        (12, 1.0 / 12.0, "12 monthly steps"),
        (30, 1.0, "30 annual steps"),
        (600, 1.0 / 12.0, "600 monthly steps"),
    };

    public static (List<Rec> Sections, Rec Precision, Rec Determinism) Projection(
        LifeInsuranceGpuExecutor ex, List<(string Name, LifeContractGpu C)> all, RefKernels.Tables tb)
    {
        var sections = new List<Rec>();
        var contracts = all.Select(x => x.C).ToArray();
        var precision = new List<object?>();
        var determinism = new List<object?>();
        foreach (var (steps, dt, label) in Configs)
        {
            var run1 = ex.ProjectBatch(contracts, steps, dt);
            var run2 = ex.ProjectBatch(contracts, steps, dt);
            var identical = true;
            for (var i = 0; i < run1.Length; i++)
                for (var t = 0; t < steps; t++)
                {
                    var a = run1[i].Steps[t]; var b = run2[i].Steps[t];
                    if (BitConverter.DoubleToInt64Bits(a.ExpectedCashflow) != BitConverter.DoubleToInt64Bits(b.ExpectedCashflow) ||
                        BitConverter.DoubleToInt64Bits(a.ProbActive) != BitConverter.DoubleToInt64Bits(b.ProbActive) ||
                        BitConverter.DoubleToInt64Bits(a.ProbDeathClaimed) != BitConverter.DoubleToInt64Bits(b.ProbDeathClaimed) ||
                        BitConverter.DoubleToInt64Bits(a.ProbLapsed) != BitConverter.DoubleToInt64Bits(b.ProbLapsed)) identical = false;
                }
            determinism.Add(new Rec { ["config"] = label, ["twoRunsBitIdentical"] = identical });

            var items = new List<object?>();
            var stats = new Dictionary<string, Gap>();
            foreach (var mode in Enum.GetValues<RefKernels.Mode>()) stats[mode.ToString()] = new Gap();
            for (var i = 0; i < run1.Length; i++)
            {
                var cf = run1[i].Steps.Select(s => s.ExpectedCashflow).ToArray();
                var pa = run1[i].Steps.Select(s => s.ProbActive).ToArray();
                var pd = run1[i].Steps.Select(s => s.ProbDeathClaimed).ToArray();
                var pl = run1[i].Steps.Select(s => s.ProbLapsed).ToArray();
                items.Add(new Rec { ["contract"] = all[i].Name, ["expectedCashflow"] = cf, ["probActive"] = pa, ["probDeathClaimed"] = pd, ["probLapsed"] = pl });
                foreach (var mode in Enum.GetValues<RefKernels.Mode>())
                {
                    var r = RefKernels.Run(contracts[i], steps, dt, tb, mode);
                    stats[mode.ToString()].Add(all[i].Name, "expectedCashflow", cf, r.Cf);
                    stats[mode.ToString()].Add(all[i].Name, "probActive", pa, r.Pa);
                    stats[mode.ToString()].Add(all[i].Name, "probDeathClaimed", pd, r.Pd);
                    stats[mode.ToString()].Add(all[i].Name, "probLapsed", pl, r.Pl);
                }
            }
            sections.Add(new Rec
            {
                ["kind"] = "projection",
                ["description"] = "LifeInsuranceGpuExecutor.ProjectBatch(contracts, timeSteps, dtYears): per contract, one value per step, in the order of the 'contracts' list of the factors section (42 benchmark contracts, then 22 edge contracts).",
                ["config"] = label,
                ["timeSteps"] = steps,
                ["dtYears"] = dt,
                ["items"] = items,
            });
            precision.Add(new Rec { ["config"] = label, ["gapVersusKernel"] = stats.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.ToRec()) });
        }
        return (sections, new Rec { ["kind"] = "precision", ["description"] = "Measured differences between the kernel's output and straightforward C# reimplementations of its formulas.", ["items"] = precision },
            new Rec { ["kind"] = "determinism", ["items"] = determinism });
    }

    private sealed class Gap
    {
        private readonly Dictionary<string, (double Abs, string AbsAt, double Rel, string RelAt)> _m = new();
        public void Add(string contract, string channel, double[] kernel, double[] reference)
        {
            _m.TryGetValue(channel, out var cur);
            for (var t = 0; t < kernel.Length; t++)
            {
                var abs = Math.Abs(kernel[t] - reference[t]);
                var rel = abs / Math.Max(Math.Abs(kernel[t]), 1e-9);
                if (abs > cur.Abs) { cur.Abs = abs; cur.AbsAt = $"{contract}[{t}]"; }
                if (rel > cur.Rel) { cur.Rel = rel; cur.RelAt = $"{contract}[{t}]"; }
            }
            _m[channel] = cur;
        }
        public Rec ToRec() => _m.ToDictionary(kv => kv.Key, kv => (object?)new Rec
        {
            ["maxAbs"] = kv.Value.Abs, ["maxAbsAt"] = kv.Value.AbsAt, ["maxRel"] = kv.Value.Rel, ["maxRelAt"] = kv.Value.RelAt,
        });
    }

    // ── (d) scenarios ───────────────────────────────────────────────────────────

    public static List<Rec> Scenarios(LifeInsuranceGpuExecutor ex, List<(string Name, LifeContractGpu C)> bench)
    {
        var portfolio = bench.Take(10).ToList();
        var contracts = portfolio.Select(x => x.C).ToArray();
        var sets = new List<(string Name, LifeScenarioSet Set, int Steps, double Dt)>
        {
            ("BaseOnly", LifeScenarioSet.BaseOnly(), 30, 1.0),
            ("BaseStressFavourable", LifeScenarioSet.BaseStressFavourable(), 30, 1.0),
            ("BaseStressFavourable monthly", LifeScenarioSet.BaseStressFavourable(), 36, 1.0 / 12.0),
            ("FromMortalityShifts custom (lapse multipliers, clamp)", LifeScenarioSet.FromMortalityShifts(new[]
            {
                (0, 100, "base"), (250, 130, "stress lapse up"), (-100, 70, "favourable lapse down"),
                (6000, 100, "shift above clamp"), (-6000, 100, "shift below clamp"), (0, 500, "lapse x5"), (0, 0, "lapse x0"),
            }), 30, 1.0),
            ("GenerateDeterministic(count=6, maxShiftBps=300, seed=1)", LifeScenarioSet.GenerateDeterministic(6, 300, 1UL), 30, 1.0),
            ("GenerateDeterministic(count=6, maxShiftBps=300, seed=2)", LifeScenarioSet.GenerateDeterministic(6, 300, 2UL), 30, 1.0),
            ("GenerateDeterministic(count=4, maxShiftBps=500, seed=42 default)", LifeScenarioSet.GenerateDeterministic(4, 500), 30, 1.0),
        };

        var result = new List<Rec>();
        foreach (var (name, set, steps, dt) in sets)
        {
            var cube = ex.ProjectScenarioBatch(contracts, set, steps, dt);
            var items = new List<object?>();
            for (var s = 0; s < cube.NumScenarios; s++)
                for (var c = 0; c < cube.NumContracts; c++)
                {
                    double[] Ch(int ch) => Enumerable.Range(0, steps).Select(t => cube[c, s, t, ch]).ToArray();
                    items.Add(new Rec
                    {
                        ["contract"] = portfolio[c].Name,
                        ["scenario"] = s,
                        ["expectedCashflow"] = Ch(LifeProjectionCube.ChannelCashflow),
                        ["probActive"] = Ch(LifeProjectionCube.ChannelProbActive),
                        ["probDeathClaimed"] = Ch(LifeProjectionCube.ChannelProbDeath),
                        ["probLapsed"] = Ch(LifeProjectionCube.ChannelProbLapsed),
                        ["totalCashflow"] = cube.TotalCashflow(c, s),
                        ["survivalProbability"] = cube.SurvivalProbability(c, s),
                    });
                }
            var perContractStats = Enumerable.Range(0, cube.NumContracts).Select(c =>
            {
                var (mn, mx, mean) = cube.CashflowStats(c);
                return (object?)new Rec { ["contract"] = portfolio[c].Name, ["min"] = mn, ["max"] = mx, ["mean"] = mean };
            }).ToList();
            var (pmin, pmax, pmean) = cube.PortfolioCashflowStats();
            result.Add(new Rec
            {
                ["kind"] = "scenarios",
                ["description"] = "LifeInsuranceGpuExecutor.ProjectScenarioBatch on the first 10 benchmark contracts, plus the LifeProjectionCube aggregations. Scenario adjustments: extraPremBps += shift (clamped to +-5000); lapse multiplier != 100 shifts yearsInForce by -(pct-100)*0.05 (floored at 0).",
                ["setName"] = name,
                ["scenarios"] = set.Scenarios.Select(x => (object?)new Rec { ["id"] = x.ScenarioId, ["mortalityShiftBps"] = x.MortalityShiftBps, ["lapseMultiplierPct"] = x.LapseMultiplierPct, ["label"] = x.Label }).ToList(),
                ["timeSteps"] = steps,
                ["dtYears"] = dt,
                ["items"] = items,
                ["portfolioCashflowAt"] = Enumerable.Range(0, cube.NumScenarios).Select(s => Enumerable.Range(0, steps).Select(t => cube.PortfolioCashflowAt(s, t)).ToArray()).ToList(),
                ["portfolioTotalCashflow"] = Enumerable.Range(0, cube.NumScenarios).Select(s => cube.PortfolioTotalCashflow(s)).ToArray(),
                ["cashflowStatsPerContract"] = perContractStats,
                ["portfolioCashflowStats"] = new Rec { ["min"] = pmin, ["max"] = pmax, ["mean"] = pmean },
            });
        }
        return result;
    }

    // ── (e) transitions ─────────────────────────────────────────────────────────

    public static Rec Transitions(LifeInsuranceGpuExecutor ex)
    {
        var graph = MarkovGraphLoader.LoadDefault();
        var e = Days(2025, 6, 1);
        var variants = new List<(string Name, Func<LifeState, LifeContractGpu> Make)>
        {
            ("base_overdue_yif3", s => Mk((uint)s, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 200_000, 120, 3f, 0, 1) with { LastPremDueDays = Days(2025, 1, 1) }),
            ("premium_not_due", s => Mk((uint)s, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 200_000, 120, 3f, 0, 2) with { LastPremDueDays = e + 30 }),
            ("grace_expired", s => Mk((uint)s, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 200_000, 120, 3f, 0, 3) with { GraceExpiryDays = e - 10 }),
            ("grace_open", s => Mk((uint)s, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 200_000, 120, 3f, 0, 4) with { GraceExpiryDays = e + 20 }),
            ("lapsed_10_months_ago", s => Mk((uint)s, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 200_000, 120, 3f, 0, 5) with { LapseDateDays = e - 300, LapseCount = 1 }),
            ("lapsed_30_months_ago", s => Mk((uint)s, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 200_000, 120, 3f, 0, 6) with { LapseDateDays = e - 900, LapseCount = 1 }),
            ("lapse_count_3", s => Mk((uint)s, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 200_000, 120, 3f, 0, 7) with { LapseDateDays = e - 100, LapseCount = 3 }),
            ("young_policy_yif1", s => Mk((uint)s, 0, InsuredGender.M, PremiumMode.Monthly, 40f, 200_000, 120, 1f, 0, 8)),
        };

        var items = new List<object?>();
        void Add(string variant, LifeContractGpu c, LifeState from, LifeState to, TransitionContext ctx, bool defined)
        {
            var r = ex.ValidateTransition(c, from, to, ctx);
            items.Add(new Rec
            {
                ["from"] = from.ToString(), ["to"] = to.ToString(), ["definedInGraph"] = defined, ["variant"] = variant,
                ["contract"] = ContractRec(variant, c),
                ["evalDateDays"] = ctx.EvalDateDays,
                ["facts"] = new Rec { ["deathReported"] = ctx.DeathReported, ["claimFiled"] = ctx.ClaimFiled, ["underwritingApproved"] = ctx.UnderwritingApproved, ["surrenderRequested"] = ctx.SurrenderRequested },
                ["allowed"] = r.Allowed, ["ruleId"] = r.RuleId, ["reason"] = r.Reason,
            });
        }
        static TransitionContext Ctx(int e, int bits) => new()
        {
            EvalDateDays = e,
            DeathReported = (bits & 1) != 0, ClaimFiled = (bits & 2) != 0, UnderwritingApproved = (bits & 4) != 0, SurrenderRequested = (bits & 8) != 0,
        };

        foreach (var t in graph.Transitions)
        {
            var from = Enum.Parse<LifeState>(t.FromState);
            var to = Enum.Parse<LifeState>(t.ToState);
            foreach (var (vn, mk) in variants)
                for (var bits = 0; bits < 16; bits++)
                    Add(vn, mk(from), from, to, Ctx(e, bits), true);
        }
        // Every ordered pair (incl. self, terminal-from, undefined), facts all false / all true.
        var definedPairs = graph.Transitions.Select(t => (t.FromState, t.ToState)).ToHashSet();
        foreach (var from in Enum.GetValues<LifeState>())
            foreach (var to in Enum.GetValues<LifeState>())
                foreach (var bits in new[] { 0, 15 })
                    Add("base_overdue_yif3", variants[0].Make(from), from, to, Ctx(e, bits), definedPairs.Contains((from.ToString(), to.ToString())));

        return new Rec
        {
            ["kind"] = "transitions",
            ["description"] = "LifeInsuranceGpuExecutor.ValidateTransition. Structural denials (self-transition, from a terminal state, undefined pair) come first; otherwise the guard rule named by the Markov transition is evaluated. NOTE: ACTUS-I maps claimApproved := claimFiled. Trace ids and timings are omitted (random / non-deterministic).",
            ["markovTransitions"] = graph.Transitions.Select(t => (object?)new Rec { ["id"] = t.Id, ["from"] = t.FromState, ["to"] = t.ToState, ["guardRuleId"] = t.GuardRuleId, ["intensityModelId"] = t.IntensityModelId }).ToList(),
            ["factsBitOrder"] = "bit0 deathReported, bit1 claimFiled, bit2 underwritingApproved, bit3 surrenderRequested (not stored; facts are spelled out per item)",
            ["evalDateDays"] = e,
            ["items"] = items,
        };
    }

    // ── (f) product rule sets ───────────────────────────────────────────────────

    public sealed record LpMeta(string OccupationClass = "Standard", string BuildingClass = "Standard", bool IsPremiumSmoker = false);
    public sealed record StMeta(bool WellnessParticipant = false, double MonthlyIncome = 0, bool HasPartnerDiscount = false);

    private sealed class LpExtractor : IProductMetaExtractor<LpMeta>
    {
        public IReadOnlyDictionary<string, DslValue> Extract(LpMeta m) => new Dictionary<string, DslValue>
        {
            ["occupation_class"] = DslValue.Of(m.OccupationClass),
            ["building_class"] = DslValue.Of(m.BuildingClass),
            ["is_premium_smoker"] = DslValue.Of(m.IsPremiumSmoker),
        };
    }

    private sealed class StExtractor : IProductMetaExtractor<StMeta>
    {
        public IReadOnlyDictionary<string, DslValue> Extract(StMeta m) => new Dictionary<string, DslValue>
        {
            ["wellness_participant"] = DslValue.Of(m.WellnessParticipant),
            ["monthly_income"] = DslValue.Of(m.MonthlyIncome),
            ["has_partner_discount"] = DslValue.Of(m.HasPartnerDiscount),
        };
    }

    /// <summary>The rule-pack JSON is read out of the ActusInsurance.LifeInsurance.Tests.GPU source file (inputs only, no expectations).</summary>
    private static string ExtractJson(string testsFile, string className)
    {
        var text = File.ReadAllText(testsFile);
        var m = Regex.Match(text, @"class " + className + @"\s*\{\s*public const string Json = """"""\r?\n(?<body>.*?)\r?\n(?<indent>[ \t]*)"""""";", RegexOptions.Singleline);
        if (!m.Success) throw new InvalidOperationException("Rule JSON not found for " + className);
        var indent = m.Groups["indent"].Value;
        return string.Join("\n", m.Groups["body"].Value.Split('\n').Select(l => l.StartsWith(indent) ? l[indent.Length..] : l));
    }

    public static List<Rec> ProductRules(string testsFile)
    {
        var lpJson = ExtractJson(testsFile, "LifestyleProtectRules");
        var stJson = ExtractJson(testsFile, "SimpleTermRules");
        var lp = ProductRuleSet<LpMeta>.FromJson(lpJson, new LpExtractor());
        var st = ProductRuleSet<StMeta>.FromJson(stJson, new StExtractor());
        var e = (double)Days(2025, 6, 1);
        var r = new Rng(20250601);
        var states = new[] { LifeState.Active, LifeState.Lapsed, LifeState.GracePeriod, LifeState.PaidUp };
        var lpRules = JsonDocument.Parse(lpJson).RootElement.GetProperty("rules").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToArray();
        var stRules = JsonDocument.Parse(stJson).RootElement.GetProperty("rules").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToArray();

        Rec Guard(GuardResult g) => new()
        {
            ["allowed"] = g.Allowed, ["reason"] = g.Reason,
            ["inputsUsed"] = g.InputsUsed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
        };

        // LifestyleProtect
        var lpItems = new List<object?>();
        var occ = new[] { "Standard", "HighRisk", "Excluded" };
        var bld = new[] { "Standard", "Rural", "HighDensityUrban" };
        var bps = new[] { 0, 100, 199, 200, 250, 299, 300, 500 };
        var sas = new[] { 200_000.0, 999_999.0, 1_000_000.0, 1_000_001.0, 5_000_000.0 };
        var yifs = new[] { 0.5f, 0.99f, 1.0f, 3.0f };
        for (var i = 0; i < 300; i++)
        {
            var meta = new LpMeta(r.Pick(occ), r.Pick(bld), r.Chance(0.4));
            var c = Mk((uint)r.Pick(states), 0, InsuredGender.M, PremiumMode.Monthly, 40f, r.Pick(sas), 120, r.Pick(yifs), r.Pick(bps), (ulong)(1000 + i));
            foreach (var rule in lpRules)
            {
                var g = lp.EvaluateGuard(rule, c, e, meta);
                lpItems.Add(new Rec { ["ruleId"] = rule, ["contract"] = ContractRec("lp", c), ["meta"] = meta, ["evalDateDays"] = e, ["result"] = Guard(g) });
            }
        }
        var lpFactors = new List<object?>();
        foreach (var (name, c) in Benchmark42().Take(10))
            lpFactors.Add(new Rec { ["contract"] = name, ["meta"] = new LpMeta(), ["factors"] = lp.EvaluateFactors(c, e, new LpMeta()).ToDictionary(kv => kv.Key, kv => (object?)kv.Value) });

        // SimpleTerm
        var stItems = new List<object?>();
        var incomes = new[] { 0.0, 500.0, 999.0, 1000.0, 1000.01, 5000.0, 20_000.0, 20_000.5, 50_000.0 };
        var sas2 = new[] { 10_000.0, 50_000.0, 200_000.0, 200_000.0, 500_000.0 };
        for (var i = 0; i < 300; i++)
        {
            var meta = new StMeta(r.Chance(0.5), r.Pick(incomes), r.Chance(0.5));
            var c = Mk((uint)r.Pick(states), 0, InsuredGender.F, PremiumMode.Monthly, 35f, r.Pick(sas2), 120, r.Pick(yifs), r.Pick(bps), (ulong)(2000 + i));
            foreach (var rule in stRules)
            {
                var g = st.EvaluateGuard(rule, c, e, meta);
                stItems.Add(new Rec { ["ruleId"] = rule, ["contract"] = ContractRec("st", c), ["meta"] = meta, ["evalDateDays"] = e, ["result"] = Guard(g) });
            }
        }

        return new()
        {
            new Rec
            {
                ["kind"] = "productRules", ["product"] = "NovaCover LifestyleProtect (LP)", ["source"] = "rule pack JSON read from ActusInsurance.LifeInsurance.Tests.GPU/ProductRuleSetTests.cs (LifestyleProtectRules.Json)",
                ["description"] = "ProductRuleSet<LifestyleProtectMeta>.EvaluateGuard over 300 seeded (meta, contract) combinations x every rule. Meta fields map to meta.occupation_class, meta.building_class, meta.is_premium_smoker.",
                ["ruleSet"] = JsonSerializer.Deserialize<JsonElement>(lpJson), ["items"] = lpItems, ["standardFactorsSample"] = lpFactors,
            },
            new Rec
            {
                ["kind"] = "productRules", ["product"] = "ClearLife SimpleTerm (ST)", ["source"] = "rule pack JSON read from ActusInsurance.LifeInsurance.Tests.GPU/ProductRuleSetTests.cs (SimpleTermRules.Json)",
                ["description"] = "ProductRuleSet<SimpleTermMeta>.EvaluateGuard over 300 seeded (meta, contract) combinations x every rule. Meta fields map to meta.wellness_participant, meta.monthly_income, meta.has_partner_discount.",
                ["ruleSet"] = JsonSerializer.Deserialize<JsonElement>(stJson), ["items"] = stItems,
            },
        };
    }
}
