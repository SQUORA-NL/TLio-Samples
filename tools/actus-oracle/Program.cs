using System.Globalization;
using ActusInsurance.LifeInsurance.GPU;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var actusGpu = Path.Combine(home, "dev", "actus", "Actus-Insurance.GPU");
var what = args.Length > 0 ? args[0] : "all";
var outDir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "out");
var genCount = args.Length > 2 ? int.Parse(args[2]) : 400;
Directory.CreateDirectory(outDir);

const string Generator = "ActusOracle (scratch console project; see README.md)";

if (what is "pam" or "all")
{
    var cases = ActusOracle.Pam.ReferenceCases(Path.Combine(actusGpu, "TestData", "actus-tests-pam.json"), out var matching);
    var gen = ActusOracle.Pam.Generated(genCount, 20260930UL);
    cases.AddRange(gen);
    var errors = cases.OfType<Dictionary<string, object?>>().Where(c => c.ContainsKey("error")).ToList();
    ActusOracle.J.WriteFile(Path.Combine(outDir, "pam-oracle.json"), Generator,
        "ActusInsurance.Core.CPU 1.0.0-preview.2 (NuGet), PrincipalAtMaturity.Schedule + Apply, .NET " + Environment.Version,
        new()
        {
            ["referenceCasesMatchingPublishedResults"] = $"{matching} of 42 (ACTUS-I's own CPU output vs actus-tests-pam.json, 10 decimals, nominalInterestRate not compared)",
            ["generatorSeed"] = 20260930UL,
            ["coverage"] = ActusOracle.Pam.Coverage.Select(c => (object?)new Dictionary<string, object?> { ["term"] = c.Term, ["varied"] = c.Varied, ["note"] = c.Note }).ToList(),
        }, cases);
    Console.WriteLine($"PAM: {cases.Count} cases ({42} reference, {gen.Count} generated); ACTUS-I matches published reference in {matching}/42; {errors.Count} errored/empty");
    foreach (var e in errors) Console.WriteLine($"  ERR {e["id"]}: {e["error"]}");
    var refMismatch = cases.Take(42).OfType<Dictionary<string, object?>>().Where(c => c.TryGetValue("matchesReference", out var m) && m is false).ToList();
    foreach (var m in refMismatch) Console.WriteLine($"  REF-MISMATCH {m["id"]}: {(m.TryGetValue("firstReferenceDifference", out var d) ? d : m.GetValueOrDefault("error"))}");
}

if (what is "life" or "all")
{
    var libDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "lib", "ActusInsurance.LifeInsurance.GPU");
    libDir = Path.GetFullPath(libDir);
    if (!Directory.Exists(libDir)) libDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "lib", "ActusInsurance.LifeInsurance.GPU"));
    using var ex = LifeInsuranceGpuExecutor.CreateDefault();
    Console.WriteLine($"Accelerator: {ex.AcceleratorName} ({ex.AcceleratorType})");

    var tb = ActusOracle.RefKernels.BuildTables();
    var bench = ActusOracle.Life.Benchmark42();
    var edge = ActusOracle.Life.Edge();
    var all = bench.Concat(edge).ToList();

    var cases = new List<object?>();
    cases.Add(ActusOracle.Life.Tables(libDir, tb));
    var factors = ActusOracle.Life.Factors(all);
    cases.Add(factors);
    var (projSections, precision, determinism) = ActusOracle.Life.Projection(ex, all, tb);
    cases.AddRange(projSections);
    cases.Add(precision);
    cases.Add(determinism);
    cases.AddRange(ActusOracle.Life.Scenarios(ex, bench));
    cases.Add(ActusOracle.Life.Transitions(ex));
    cases.AddRange(ActusOracle.Life.ProductRules(Path.Combine(actusGpu, "src", "ActusInsurance.LifeInsurance.Tests.GPU", "ProductRuleSetTests.cs")));

    ActusOracle.J.WriteFile(Path.Combine(outDir, "life-oracle.json"), Generator,
        $"ILGPU 1.5.3, accelerator: {ex.AcceleratorName} ({ex.AcceleratorType}); ActusInsurance.LifeInsurance.GPU from source (scratch copy), .NET {Environment.Version}",
        new()
        {
            ["contractOrder"] = all.Select(x => (object?)x.Name).ToList(),
            ["note"] = "'contractOrder' is the row order of every 'projection' section's items (42 benchmark contracts, then 22 edge contracts).",
        }, cases, lineDepth: 4);

    foreach (var c in new[] { precision, determinism })
        Console.WriteLine(ActusOracle.J.Emit(c, 6));
}
