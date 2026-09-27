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

    private static (long ElapsedMs, long EventCount) RunPortfolio(CompiledScript<JToken> script, int contractCount)
    {
        var eventTotal = 0L;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < contractCount; i++)
        {
            var context = JsonExecutionContext.CreateDefault();
            var result = script.Execute(BuildContract(i), context);
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
}
