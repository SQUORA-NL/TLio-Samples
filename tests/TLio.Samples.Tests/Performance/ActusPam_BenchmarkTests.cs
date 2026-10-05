using System.Diagnostics;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TLio.Client;
using TLio.Extensions.Looping;
using TLio.Extensions.Math;
using TLio.Extensions.Text;
using TLio.Extensions.TimeDate;
using TLio.Json;

namespace TLio.Samples.Tests.Performance;

/// <summary>
/// Wall-clock throughput of the ACTUS PAM sample's own script
/// (<c>samples/TLio.Sample.Actus.Api/Scripts/pam-simple.json</c>, read from disk here — not a
/// copy — so this always benchmarks the real script) run over a portfolio of contracts, one
/// <see cref="CompiledScript{TNode}.Execute"/> per contract against a fresh
/// <see cref="TLio.Core.Contracts.IExecutionContext{TNode}"/>, exactly the shape that sample's
/// own <c>Program.cs</c> uses per HTTP request and a batch valuation job would use per contract.
/// The script is compiled once and reused across the whole portfolio, as that sample's host does.
///
/// This started life in TLio itself (github.com/SQUORA-NL/TLio), where building it surfaced four
/// real concurrency/throughput bugs in the engine (a shallow <c>Clone()</c> that shared nested
/// <c>while</c>/<c>forEach</c>/<c>ifElse</c> command state across concurrent executions, a
/// literal value handed out by reference instead of cloned, a process-wide lock in the path
/// cache, and — the dominant one — <c>JsonNodeAdapter.TryGetDouble</c> using exceptions as its
/// "is this numeric" test, found via <c>dotnet-trace</c>). Fixed there; this benchmark moved
/// here once the fix shipped, because TLio itself has no notion of ACTUS or PAM contracts — this
/// is exactly the kind of domain-shaped, real-script benchmark that belongs with the sample it
/// benchmarks, not in the library.
///
/// This is a benchmark, not a regression gate — [Explicit] because a 100k/1M-contract run takes
/// real wall-clock minutes and has no "fast enough" pass/fail line. Run manually to get numbers
/// for this machine:
///
///   DOTNET_gcServer=1 dotnet test tests/TLio.Samples.Tests -c Release --filter "FullyQualifiedName~ActusPam_BenchmarkTests"
///
/// (Release matters: Debug JIT/tiering makes the larger portfolios several times slower.
/// DOTNET_gcServer=1 matters for <see cref="Portfolio_Throughput_Parallel"/> specifically: this
/// workload allocates a full JSON document tree per contract, and Workstation GC — the default
/// for a console/test host — measures roughly half the throughput Server GC does here, since
/// Server GC gives each core its own heap instead of coordinating collections through one.)
/// </summary>
[TestFixture]
[Explicit("Benchmark, not a CI gate - run manually for portfolio throughput numbers.")]
public class ActusPam_BenchmarkTests
{
    private static readonly string ScriptPath = Path.Combine(
        RepositoryRoot, "samples", "TLio.Sample.Actus.Api", "Scripts", "pam-simple.json");

    private static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "TLio.Sample.Actus.Api")))
                dir = dir.Parent;

            return dir?.FullName
                ?? throw new DirectoryNotFoundException("samples/TLio.Sample.Actus.Api is not above the test directory.");
        }
    }

    private static ScriptEngine<JToken> CreateEngine()
    {
        var options = ParseOptions<JToken>.CreateDefault();
        options.FunctionsProvider.RegisterMath<JToken>();
        options.FunctionsProvider.RegisterText<JToken>();
        options.FunctionsProvider.RegisterTimeDate<JToken>();
        options.CommandsProvider.RegisterLooping<JToken>();
        return new ScriptEngine<JToken>(options.CommandsProvider, options.FunctionsProvider);
    }

    /// <summary>
    /// A 10-year loan with a quarterly interest-payment cycle -> 41 schedule dates (IED, 39 IP,
    /// MD), representative of a real PAM instrument rather than a 1-2 payment toy contract.
    /// Terms vary slightly per index so the portfolio isn't 1,000,000 copies of one value.
    /// </summary>
    private static JToken BuildContract(int index) =>
        new JObject
        {
            ["contractId"] = $"PAM{index:D7}",
            ["contractRole"] = index % 2 == 0 ? "RPA" : "RPL",
            ["currency"] = "EUR",
            ["notionalPrincipal"] = 10_000 + index % 90_000,
            ["nominalInterestRate"] = 0.01 + index % 50 / 1000.0,
            ["dayCountConvention"] = "A360",
            ["initialExchangeDate"] = "2020-01-01",
            ["maturityDate"] = "2030-01-01",
            ["interestPaymentCycle"] = new JObject { ["count"] = 3, ["unit"] = "months" },
        };

    /// <summary>
    /// The ACTUS-I "Ultimate" PAM workload (<c>UltimatePamData</c> in its benchmark project): one
    /// fixed-rate contract, 50 years, monthly interest, notional 100,000 at 5 %, A365. ACTUS-I
    /// counts 602 events (IED + 600 IP + MD); here the last IP and MD fall on the same date and
    /// come out as one MD event, so 601.
    /// </summary>
    private static JToken BuildContract50y(int index) =>
        new JObject
        {
            ["contractId"] = $"ULT50Y{index:D7}",
            ["contractRole"] = "RPA",
            ["currency"] = "USD",
            ["notionalPrincipal"] = 100_000,
            ["nominalInterestRate"] = 0.05,
            ["dayCountConvention"] = "A365",
            ["initialExchangeDate"] = "2025-01-01",
            ["maturityDate"] = "2075-01-01",
            ["interestPaymentCycle"] = new JObject { ["count"] = 1, ["unit"] = "months" },
        };

    private static (long ElapsedMs, long EventCount) RunPortfolio(CompiledScript<JToken> script, int contractCount) =>
        RunPortfolio(script, contractCount, BuildContract);

    private static (long ElapsedMs, long EventCount) RunPortfolio(
        CompiledScript<JToken> script, int contractCount, Func<int, JToken> build)
    {
        var eventTotal = 0L;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < contractCount; i++)
        {
            var context = JsonExecutionContext.CreateDefault();
            var result = script.Execute(build(i), context);
            if (!result.Success)
            {
                var reasons = string.Join("; ", context.GetLogEntries().Select(e => $"{e.Level}: {e.Message}"));
                throw new InvalidOperationException($"Contract {i} failed to execute: {reasons}");
            }
            eventTotal += result.Data.SelectToken("$.summary.eventCount")!.Value<int>();
        }
        sw.Stop();
        return (sw.ElapsedMilliseconds, eventTotal);
    }

    [TestCase(100)]
    [TestCase(10_000)]
    [TestCase(100_000)]
    [TestCase(1_000_000)]
    public void Portfolio_Throughput(int contractCount)
    {
        var adapter = JsonExecutionContext.CreateDefault().NodeAdapter;
        var engine = CreateEngine();
        var script = engine.Compile(File.ReadAllText(ScriptPath), adapter);

        // Warmup: pays for JIT/tiering once, outside the timed run.
        RunPortfolio(script, System.Math.Min(500, contractCount));

        var (elapsedMs, eventTotal) = RunPortfolio(script, contractCount);
        var perContractUs = elapsedMs * 1000.0 / contractCount;
        var contractsPerSecond = elapsedMs == 0 ? double.PositiveInfinity : contractCount / (elapsedMs / 1000.0);

        TestContext.WriteLine(
            $"portfolio={contractCount:N0} contracts | total={elapsedMs:N0} ms | " +
            $"per-contract={perContractUs:F2} us | throughput={contractsPerSecond:N0} contracts/sec | " +
            $"events={eventTotal:N0}");

        Assert.That(eventTotal, Is.EqualTo(41L * contractCount), "each contract's schedule should be IED + 39 IP + MD = 41 events");
    }

    /// <summary>
    /// Same script, same portfolio shape, but contracts are independent so nothing stops running
    /// them across all cores: the script is compiled exactly once — one shared
    /// <see cref="CompiledScript{JToken}"/>, exactly as this sample's own <c>Program.cs</c> holds
    /// one per endpoint for the life of the process — and every contract calls
    /// <see cref="CompiledScript{TNode}.Execute"/> on that same shared instance concurrently, each
    /// with its own fresh <see cref="TLio.Core.Contracts.IExecutionContext{TNode}"/> and input
    /// <see cref="JToken"/>. Requires the TLio fix described in this class's own remarks; against
    /// an older TLio package this can corrupt results or throw under concurrency.
    /// </summary>
    [TestCase(100)]
    [TestCase(10_000)]
    [TestCase(100_000)]
    [TestCase(1_000_000)]
    public void Portfolio_Throughput_Parallel(int contractCount)
    {
        var adapter = JsonExecutionContext.CreateDefault().NodeAdapter;
        var engine = CreateEngine();
        var script = engine.Compile(File.ReadAllText(ScriptPath), adapter);

        // Warmup: pays for JIT/tiering once, outside the timed parallel run.
        RunPortfolio(script, System.Math.Min(500, contractCount));

        // ParallelOptions.MaxDegreeOfParallelism is a ceiling, not a guarantee: the ThreadPool
        // otherwise injects worker threads gradually ("hill-climbing") rather than starting a
        // short-lived, CPU-bound run with as many as it will ever need.
        ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount);

        var eventTotal = 0L;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, contractCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var context = JsonExecutionContext.CreateDefault();
                var result = script.Execute(BuildContract(i), context);
                if (!result.Success)
                {
                    var reasons = string.Join("; ", context.GetLogEntries().Select(e => $"{e.Level}: {e.Message}"));
                    throw new InvalidOperationException($"Contract {i} failed to execute: {reasons}");
                }
                Interlocked.Add(ref eventTotal, result.Data.SelectToken("$.summary.eventCount")!.Value<int>());
            });
        sw.Stop();

        var perContractUs = sw.ElapsedMilliseconds * 1000.0 / contractCount;
        var contractsPerSecond = sw.ElapsedMilliseconds == 0
            ? double.PositiveInfinity
            : contractCount / (sw.ElapsedMilliseconds / 1000.0);

        TestContext.WriteLine(
            $"portfolio={contractCount:N0} contracts | cores={Environment.ProcessorCount} | " +
            $"total={sw.ElapsedMilliseconds:N0} ms | per-contract={perContractUs:F2} us | " +
            $"throughput={contractsPerSecond:N0} contracts/sec | events={eventTotal:N0}");

        Assert.That(eventTotal, Is.EqualTo(41L * contractCount), "each contract's schedule should be IED + 39 IP + MD = 41 events");
    }

    /// <summary>
    /// The 50-year monthly workload ACTUS-I's GPU/CPU numbers are measured on: 601 events per
    /// contract against 41 above, about 15x the work. Sequential.
    /// </summary>
    [TestCase(100)]
    [TestCase(1_000)]
    [TestCase(10_000)]
    public void Portfolio50y_Throughput(int contractCount)
    {
        var script = CreateEngine().Compile(File.ReadAllText(ScriptPath), JsonExecutionContext.CreateDefault().NodeAdapter);
        RunPortfolio(script, System.Math.Min(50, contractCount), BuildContract50y);

        var (elapsedMs, eventTotal) = RunPortfolio(script, contractCount, BuildContract50y);
        TestContext.WriteLine(
            $"shape=50y-monthly mode=seq portfolio={contractCount:N0} | total={elapsedMs:N0} ms | " +
            $"per-contract={elapsedMs * 1000.0 / contractCount:F2} us | events={eventTotal:N0}");
        Assert.That(eventTotal, Is.EqualTo(601L * contractCount), "IED + 599 IP + MD (last IP and MD share a date) = 601 events");
    }

    /// <summary>
    /// <c>pam-reference.json</c>, the script that reproduces all 42 ACTUS reference cases, across
    /// all cores. It does the full PAM semantics per event (ACTUS-I's scope), so this is the
    /// like-for-like counterpart of ACTUS-I CPU. 42 events for the 10-year quarterly shape and
    /// 602 for the 50-year monthly one (it emits the maturity IP separately, like ACTUS-I).
    /// </summary>
    [TestCase(false, 1_000)]
    [TestCase(false, 10_000)]
    [TestCase(false, 100_000)]
    [TestCase(true, 100)]
    [TestCase(true, 1_000)]
    [TestCase(true, 10_000)]
    [TestCase(true, 100_000)]
    public void PortfolioReference_Throughput_Parallel(bool fiftyYearMonthly, int contractCount)
    {
        var reference = Path.Combine(Path.GetDirectoryName(ScriptPath)!, "pam-reference.json");
        var script = CreateEngine().Compile(File.ReadAllText(reference), JsonExecutionContext.CreateDefault().NodeAdapter);
        var expectedPerContract = fiftyYearMonthly ? 602 : 42;

        JToken Build(int i)
        {
            var c = fiftyYearMonthly ? BuildContract50y(i) : BuildContract(i);
            var cycle = (JObject)c["interestPaymentCycle"]!;
            cycle["longStub"] = true;
            c["interestPaymentAnchor"] = DateTime.Parse((string)c["initialExchangeDate"]!, System.Globalization.CultureInfo.InvariantCulture)
                .AddMonths((int)cycle["count"]!).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            c["statusDate"] = c["initialExchangeDate"];
            c["endOfMonth"] = false;
            return c;
        }

        long Execute(int i)
        {
            var context = JsonExecutionContext.CreateDefault();
            var result = script.Execute(Build(i), context);
            if (!result.Success)
                throw new InvalidOperationException($"Contract {i} failed to execute.");
            return result.Data.SelectToken("$.events")!.Count();
        }

        for (var i = 0; i < System.Math.Min(20, contractCount); i++) Execute(i); // warmup
        ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount);

        var eventTotal = 0L;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, contractCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i => Interlocked.Add(ref eventTotal, Execute(i)));
        sw.Stop();

        TestContext.WriteLine(
            $"shape=reference-{(fiftyYearMonthly ? "50y-monthly" : "10y-quarterly")} mode=par cores={Environment.ProcessorCount} " +
            $"portfolio={contractCount:N0} | total={sw.ElapsedMilliseconds:N0} ms | " +
            $"per-contract={sw.ElapsedMilliseconds * 1000.0 / contractCount:F2} us | events={eventTotal:N0}");
        Assert.That(eventTotal, Is.EqualTo((long)expectedPerContract * contractCount));
    }

    /// <summary>
    /// <c>life-project.json</c> over ACTUS-I's 42-policy benchmark cycle: 30 annual steps (the
    /// horizon of its published 100,000-policy row) or 600 monthly steps (its "Ultimate"
    /// workload). Sequential when <paramref name="parallel"/> is false.
    /// </summary>
    [TestCase(30, false, 10_000)]
    [TestCase(30, true, 100_000)]
    [TestCase(600, false, 1_000)]
    [TestCase(600, true, 10_000)]
    public void PortfolioLife_Throughput(int steps, bool parallel, int contractCount)
    {
        var lifeScript = Path.Combine(Path.GetDirectoryName(ScriptPath)!, "life-project.json");
        var script = CreateEngine().Compile(File.ReadAllText(lifeScript), JsonExecutionContext.CreateDefault().NodeAdapter);
        var dt = steps == 600 ? 1.0 / 12.0 : 1.0;

        var ages = new[] { 25.0, 35, 40, 45, 50, 55, 60, 65 };
        var policies = new List<JObject>(42);
        var index = 0;
        foreach (var age in ages)
            for (var gender = 0; gender < 3; gender++)
                foreach (var smoker in new[] { 0, 1 })
                {
                    if (++index > 42) break;
                    var sumAssured = (index % 4) switch { 0 => 50_000.0, 1 => 100_000.0, 2 => 200_000.0, _ => 500_000.0 };
                    policies.Add(new JObject
                    {
                        ["currentState"] = 1, ["smokerStatus"] = smoker, ["insuredGender"] = gender,
                        ["premiumMode"] = index % 3, ["ageAtEval"] = age, ["sumAssured"] = sumAssured,
                        ["premiumAmount"] = sumAssured * 0.001,
                        ["yearsInForce"] = (index % 4) switch { 0 => 0.5, 1 => 2.0, 2 => 5.0, _ => 10.0 },
                        ["extraPremBps"] = (index % 4) switch { 0 => 0, 1 => 100, 2 => 200, _ => 500 },
                    });
                }

        long Execute(int i)
        {
            var input = new JObject { ["contract"] = policies[i % 42].DeepClone(), ["timeSteps"] = steps, ["dtYears"] = dt };
            var context = JsonExecutionContext.CreateDefault();
            var result = script.Execute(input, context);
            if (!result.Success)
                throw new InvalidOperationException($"Policy {i} failed to execute.");
            return result.Data.SelectToken("$.steps")!.Count();
        }

        for (var i = 0; i < System.Math.Min(42, contractCount); i++) Execute(i); // warmup
        ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount);

        var cells = 0L;
        var sw = Stopwatch.StartNew();
        if (parallel)
            Parallel.For(0, contractCount,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                i => Interlocked.Add(ref cells, Execute(i)));
        else
            for (var i = 0; i < contractCount; i++) cells += Execute(i);
        sw.Stop();

        TestContext.WriteLine(
            $"shape=life-{steps}steps mode={(parallel ? "par" : "seq")} cores={(parallel ? Environment.ProcessorCount : 1)} " +
            $"portfolio={contractCount:N0} | total={sw.ElapsedMilliseconds:N0} ms | " +
            $"per-policy={sw.ElapsedMilliseconds * 1000.0 / contractCount:F2} us | cells={cells:N0}");
        Assert.That(cells, Is.EqualTo((long)steps * contractCount));
    }

    /// <summary>
    /// ACTUS-I's own portfolio-sweep workload: its 42 PAM reference contracts cycled to any batch
    /// size (<c>PamPortfolioSweepBenchmarks</c>, 520 events per 42-contract cycle), through
    /// <c>pam-reference.json</c>. Likely the workload behind ACTUS-I's published PAM rows.
    /// </summary>
    [TestCase(false, 10_000)]
    [TestCase(true, 10_000)]
    [TestCase(true, 100_000)]
    public void PortfolioCycledReference_Throughput(bool parallel, int contractCount)
    {
        var (script, cases) = LoadCycledReference();

        long Execute(int i)
        {
            var context = JsonExecutionContext.CreateDefault();
            var result = script.Execute(cases[i % cases.Count].DeepClone(), context);
            if (!result.Success)
                throw new InvalidOperationException($"Contract {i} failed to execute.");
            return result.Data.SelectToken("$.events")!.Count();
        }

        var perCycle = Enumerable.Range(0, cases.Count).Sum(Execute);
        Assert.That(perCycle, Is.EqualTo(520L), "ACTUS-I emits 520 events for the 42 reference contracts");
        for (var i = 0; i < 500; i++) Execute(i); // warmup
        ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount);

        var events = 0L;
        var sw = Stopwatch.StartNew();
        if (parallel)
            Parallel.For(0, contractCount,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                i => Interlocked.Add(ref events, Execute(i)));
        else
            for (var i = 0; i < contractCount; i++) events += Execute(i);
        sw.Stop();

        TestContext.WriteLine(
            $"shape=ref42 mode={(parallel ? "par" : "seq")} cores={(parallel ? Environment.ProcessorCount : 1)} " +
            $"portfolio={contractCount:N0} | total={sw.ElapsedMilliseconds:N0} ms | " +
            $"per-contract={sw.ElapsedMilliseconds * 1000.0 / contractCount:F2} us | events={events:N0}");
    }

    /// <summary>One warmed contract of the cycled reference set, mean/median/p99 over 4,000 runs.</summary>
    [Test]
    public void PortfolioCycledReference_SingleContract()
    {
        var (script, cases) = LoadCycledReference();
        for (var i = 0; i < 2000; i++)
            script.Execute(cases[i % cases.Count].DeepClone(), JsonExecutionContext.CreateDefault());

        var micros = new double[4000];
        for (var i = 0; i < micros.Length; i++)
        {
            var input = cases[i % cases.Count].DeepClone();
            var context = JsonExecutionContext.CreateDefault();
            var t = Stopwatch.GetTimestamp();
            script.Execute(input, context);
            micros[i] = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
        }
        Array.Sort(micros);
        TestContext.WriteLine(
            $"shape=ref42 mode=single mean_us={micros.Average():F1} median_us={micros[micros.Length / 2]:F1} " +
            $"p99_us={micros[(int)(micros.Length * 0.99)]:F1}");
    }

    private static (CompiledScript<JToken> Script, List<JObject> Cases) LoadCycledReference()
    {
        var reference = Path.Combine(Path.GetDirectoryName(ScriptPath)!, "pam-reference.json");
        var script = CreateEngine().Compile(File.ReadAllText(reference), JsonExecutionContext.CreateDefault().NodeAdapter);
        var file = Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources", "actus-tests-pam.json");
        var cases = JObject.Parse(File.ReadAllText(file)).Properties()
            .Select(p => ActusPamOracleTests.BuildInput((JObject)p.Value))
            .ToList();
        return (script, cases);
    }

    /// <summary>Same 50-year workload across all cores, one shared compiled script.</summary>
    [TestCase(100)]
    [TestCase(10_000)]
    [TestCase(100_000)]
    public void Portfolio50y_Throughput_Parallel(int contractCount)
    {
        var script = CreateEngine().Compile(File.ReadAllText(ScriptPath), JsonExecutionContext.CreateDefault().NodeAdapter);
        RunPortfolio(script, System.Math.Min(50, contractCount), BuildContract50y);
        ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount);

        var eventTotal = 0L;
        var sw = Stopwatch.StartNew();
        Parallel.For(0, contractCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var context = JsonExecutionContext.CreateDefault();
                var result = script.Execute(BuildContract50y(i), context);
                if (!result.Success)
                {
                    var reasons = string.Join("; ", context.GetLogEntries().Select(e => $"{e.Level}: {e.Message}"));
                    throw new InvalidOperationException($"Contract {i} failed to execute: {reasons}");
                }
                Interlocked.Add(ref eventTotal, result.Data.SelectToken("$.summary.eventCount")!.Value<int>());
            });
        sw.Stop();

        TestContext.WriteLine(
            $"shape=50y-monthly mode=par cores={Environment.ProcessorCount} portfolio={contractCount:N0} | " +
            $"total={sw.ElapsedMilliseconds:N0} ms | per-contract={sw.ElapsedMilliseconds * 1000.0 / contractCount:F2} us | events={eventTotal:N0}");
        Assert.That(eventTotal, Is.EqualTo(601L * contractCount));
    }
}
