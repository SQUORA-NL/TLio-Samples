using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TLio.Client;
using TLio.Core.Contracts;
using TLio.Core.Models;
using TLio.Extensions.Looping;
using TLio.Extensions.Math;
using TLio.Extensions.Text;
using TLio.Extensions.TimeDate;
using TLio.Json;

namespace TLio.Samples.Tests;

/// <summary>
/// Field-level lineage over a Tlio script, as a tool over the command list. The engine itself does
/// not offer this: what it offers is a per-execution trace (see <see cref="NativeTrace_*"/>), one
/// entry per command run with its name, target path, outcome and a message. It carries no source
/// expression, no position in the script and nothing about which field a value came from.
///
/// <see cref="PamLineage"/> reads the script text instead. A command list already says where every
/// value came from and where it went, so "what writes this field, from what, under which
/// conditions" is answered by walking it: no instrumentation. It is static, so it says what CAN
/// write a field, not what did on one run, and it never invents resolved array indices.
///
/// Positions are 1-based command numbers: <c>#15</c> is the 15th top-level command,
/// <c>#13.commands[2]</c> the 2nd command in the body of the 13th.
/// </summary>
[TestFixture]
public class ActusPamLineageTests
{
    // ── The tool ─────────────────────────────────────────────────────────────────

    internal static class PamLineage
    {
        public enum Kind { None, Exact, Literal, Wholesale, Inside }

        public sealed record Writer(
            string At, string Verb, string Target, JToken? Value, string? Source,
            IReadOnlyList<string> Guards, IReadOnlyList<string> Reads);

        public sealed record Hit(Writer Writer, Kind Kind, string? Source);

        public sealed record Explanation(string Field, IReadOnlyList<Hit> Writers, IReadOnlyList<Hit> RemovedBy);

        private static readonly Regex PathToken = new(
            @"(?<![A-Za-z0-9_])(?:\$|@)(?:\.[A-Za-z_][A-Za-z0-9_]*|\[[^\]]*\])*", RegexOptions.Compiled);
        private static readonly Regex Filter = new(@"\[\?\(.*?\)\]", RegexOptions.Compiled);
        private static readonly Regex SimpleKeys = new(@"^(\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.Compiled);

        /// <summary>Every command in the script in reading order, nested bodies included.</summary>
        public static List<Writer> Walk(JArray script)
        {
            var all = new List<Writer>();
            Walk(script, "", "", [], null, all);
            return all;
        }

        private static void Walk(JArray commands, string prefix, string listName, List<string> guards,
            string? loopPath, List<Writer> all)
        {
            for (var i = 0; i < commands.Count; i++)
            {
                if (commands[i] is not JObject cmd || cmd["command"] is null) continue;
                var at = prefix.Length == 0 ? $"#{i + 1}" : $"{prefix}.{listName}[{i + 1}]";
                var verb = (string)cmd["command"]!;

                if (verb is "forEach" or "while" or "ifElse")
                {
                    var head = verb switch
                    {
                        "forEach" => $"forEach {cmd["path"]}",
                        "while" => $"while {cmd["condition"]}",
                        _ => $"{cmd["condition"]}",
                    };
                    var childLoop = verb == "forEach" ? Resolve((string?)cmd["path"], loopPath) : loopPath;
                    foreach (var prop in cmd.Properties())
                    {
                        if (prop.Value is not JArray body || !body.OfType<JObject>().Any(o => o["command"] is not null)) continue;
                        var label = prop.Name == "elseScript" ? $"else of {head}" : verb == "ifElse" ? $"if {head}" : head;
                        Walk(body, at, prop.Name, [.. guards, label], childLoop, all);
                    }
                    // The condition or loop path is read by everything inside it, so it is a guard already.
                    continue;
                }

                var rawTarget = (string?)cmd["path"] ?? (string?)cmd["toPath"];
                if (rawTarget is null) continue;
                var target = Resolve(rawTarget, loopPath)!;
                var localGuards = rawTarget.Contains("[?(") ? [.. guards, $"where {rawTarget}"] : guards;
                var value = cmd["fromPath"] ?? cmd["value"];
                var source = value is null ? null
                    : value.Type == JTokenType.String ? (string)value! : value.ToString(Formatting.None);

                var reads = new List<string>();
                foreach (var text in localGuards.Concat(source is null ? [] : [source]))
                    reads.AddRange(ReadsOf(text, loopPath));
                all.Add(new Writer(at, verb, target, value, source, localGuards, reads.Distinct().ToList()));
            }
        }

        /// <summary>"@" and "@.x" become the enclosing forEach's items, "[?(..)]" becomes "[*]" (the filter is kept as a guard).</summary>
        private static string? Resolve(string? path, string? loopPath)
        {
            if (path is null) return null;
            var p = Filter.Replace(path.Trim(), "[*]");
            if (p == "@") return loopPath is null ? "@" : loopPath + "[*]";
            if (p.StartsWith("@.") || p.StartsWith("@["))
                return (loopPath is null ? "@" : loopPath + "[*]") + p[1..];
            return p;
        }

        private static IEnumerable<string> ReadsOf(string text, string? loopPath)
        {
            foreach (Match m in PathToken.Matches(text))
                if (m.Value != "$") yield return Resolve(m.Value, loopPath)!;
            if (loopPath is not null && text.Contains("scriptpath()"))
                yield return loopPath + "[*]";
        }

        private static bool IsPrefix(string parent, string child) =>
            child.Length > parent.Length && child.StartsWith(parent, StringComparison.Ordinal)
            && (child[parent.Length] == '.' || child[parent.Length] == '[');

        public static (Kind, string?) Match(Writer w, string q)
        {
            if (w.Target == q) return (Kind.Exact, w.Source);
            if (IsPrefix(w.Target, q))
            {
                var rest = q[w.Target.Length..];
                if (w.Value is JObject literal && SimpleKeys.IsMatch(rest))
                {
                    JToken? at = literal;
                    foreach (var key in rest.Split('.', StringSplitOptions.RemoveEmptyEntries))
                        at = (at as JObject)?[key];
                    return at is null ? (Kind.None, null)
                        : (Kind.Literal, at.Type == JTokenType.String ? (string)at! : at.ToString(Formatting.None));
                }
                return (Kind.Wholesale, w.Source);
            }
            return IsPrefix(q, w.Target) ? (Kind.Inside, w.Source) : (Kind.None, null);
        }

        public static Explanation Explain(JArray script, string field)
        {
            var writers = new List<Hit>();
            var removed = new List<Hit>();
            foreach (var w in Walk(script))
            {
                var (kind, source) = Match(w, field);
                if (kind is Kind.None or Kind.Inside) continue;
                (w.Verb == "remove" ? removed : writers).Add(new Hit(w, kind, source));
            }
            return new Explanation(field, writers, removed);
        }

        /// <summary>
        /// What a value can depend on. Sound rather than exact: all writers count, guarded or not, and
        /// so do the conditions, so a listed input is one the value MAY depend on.
        /// <c>Inputs</c>: no command writes the path. <c>Defaults</c>: only an <c>add</c> of a literal
        /// writes it, and <c>add</c> skips when the caller already supplied the field, so it is an input
        /// with a default. <c>Preemptable</c>: written by some other <c>add</c>, so a caller who supplies
        /// the field bypasses the command.
        /// </summary>
        public sealed record Dependencies(SortedSet<string> Inputs, SortedSet<string> Defaults, SortedSet<string> Preemptable)
        {
            public IEnumerable<string> Callers => Inputs.Concat(Defaults);
        }

        public static Dependencies InputsOf(JArray script, string field)
        {
            var all = Walk(script);
            var inputs = new SortedSet<string>(StringComparer.Ordinal);
            var defaults = new SortedSet<string>(StringComparer.Ordinal);
            var preemptable = new SortedSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>();
            var stack = new Stack<string>([field]);
            while (stack.Count > 0)
            {
                var q = stack.Pop();
                if (!seen.Add(q)) continue;
                var writers = all.Where(w => w.Verb != "remove" && Match(w, q).Item1 != Kind.None).ToList();
                if (writers.Count == 0) { inputs.Add(q); continue; }
                var adds = writers.Where(w => w.Verb == "add" && w.Target == q).ToList();
                if (q != field && adds.Count > 0)
                {
                    var onlyLiteralAdds = writers.All(w => adds.Contains(w) && IsLiteral(w.Value));
                    (onlyLiteralAdds ? defaults : preemptable).Add(q);
                }
                foreach (var r in writers.SelectMany(w => w.Reads)) stack.Push(r);
            }
            return new Dependencies(inputs, defaults, preemptable);
        }

        private static bool IsLiteral(JToken? value) =>
            value is JValue v && (v.Type != JTokenType.String
                || (v.Value is string s && !s.StartsWith('=') && !s.StartsWith('$') && !s.StartsWith('@')));

        /// <summary>Formatted for a projector, not for a log aggregator.</summary>
        public static string Render(JArray script, string field)
        {
            var e = Explain(script, field);
            var sb = new StringBuilder();
            sb.AppendLine($"field       {field}");
            if (e.Writers.Count == 0)
            {
                sb.AppendLine("written by  nothing in this version writes that path");
            }
            else
            {
                var last = e.Writers[^1];
                var how = last.Kind switch
                {
                    Kind.Exact => last.Writer.Verb,
                    Kind.Literal => $"{last.Writer.Verb}, inside an object literal written to {last.Writer.Target}",
                    _ => $"{last.Writer.Verb}, as part of {last.Writer.Target} (contents not known statically)",
                };
                sb.AppendLine($"written by  {last.Writer.At}  {how}");
                sb.AppendLine($"source      {last.Source ?? "(none)"}");
                sb.AppendLine(last.Writer.Guards.Count == 0
                    ? "runs        every time"
                    : $"runs        only under: {string.Join("  >  ", last.Writer.Guards)}");
                if (e.Writers.Count > 1)
                    sb.AppendLine($"and         {e.Writers.Count - 1} earlier writer(s): {string.Join(", ", e.Writers.Take(e.Writers.Count - 1).Select(h => h.Writer.At))}");
            }
            foreach (var r in e.RemovedBy)
                sb.AppendLine($"removed by  {r.Writer.At}{(r.Writer.Guards.Count > 0 ? "  (" + string.Join(" > ", r.Writer.Guards) + ")" : "")}");
            if (e.Writers.Any(h => h.Kind == Kind.Exact && h.Writer.Verb == "add"))
                sb.AppendLine("note        written with add: an input that already has this field wins");
            if (e.Writers.Count > 0)
            {
                var deps = InputsOf(script, field);
                sb.AppendLine($"inputs      {string.Join(", ", deps.Inputs)}");
                if (deps.Defaults.Count > 0)
                    sb.AppendLine($"defaults    {string.Join(", ", deps.Defaults)}   (used when the caller sends nothing)");
                if (deps.Preemptable.Count > 0)
                    sb.AppendLine($"bypassable  {deps.Preemptable.Count} intermediate field(s) written with add; a caller who sends them skips the command");
            }
            return sb.ToString().TrimEnd();
        }
    }

    // ── Loading and running ──────────────────────────────────────────────────────

    private static string ScriptFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "TLio.Sample.Actus.Api")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples", "TLio.Sample.Actus.Api", "Scripts", name);
    }

    private static JArray Load(string name) => JArray.Parse(File.ReadAllText(ScriptFile(name)));

    private static ScriptEngine<JToken> Engine()
    {
        var options = ParseOptions<JToken>.CreateDefault();
        options.FunctionsProvider.RegisterMath<JToken>();
        options.FunctionsProvider.RegisterText<JToken>();
        options.FunctionsProvider.RegisterTimeDate<JToken>();
        options.CommandsProvider.RegisterLooping<JToken>();
        return new ScriptEngine<JToken>(options.CommandsProvider, options.FunctionsProvider);
    }

    private static JToken Run(string script, JObject input, ITraceCollector? trace = null)
    {
        var compiled = Engine().Compile(File.ReadAllText(ScriptFile(script)), JsonExecutionContext.CreateDefault().NodeAdapter);
        var context = JsonExecutionContext.CreateDefault();
        context.TraceCollector = trace;
        var result = compiled.Execute(input, context);
        Assert.That(result.Success, Is.True, string.Join("; ", context.GetLogEntries().Select(e => e.Message)));
        return result.Data;
    }

    private static JObject SimpleContract() => new()
    {
        ["contractId"] = "L1", ["contractRole"] = "RPA", ["currency"] = "EUR",
        ["notionalPrincipal"] = 10000, ["nominalInterestRate"] = 0.05, ["dayCountConvention"] = "A360",
        ["initialExchangeDate"] = "2020-01-01", ["maturityDate"] = "2021-01-01",
        ["interestPaymentCycle"] = new JObject { ["count"] = 3, ["unit"] = "months" },
    };

    private sealed class Collector : ITraceCollector
    {
        public readonly List<TraceEntry> Entries = [];
        public void Record(TraceEntry entry) => Entries.Add(entry);
    }

    // ── What the engine gives natively ───────────────────────────────────────────

    [Test]
    public void NativeTrace_IsPerCommandNotPerField()
    {
        var trace = new Collector();
        Run("pam-simple.json", SimpleContract(), trace);

        // One entry per command that RAN, loop bodies once per iteration: more entries than commands.
        Assert.That(trace.Entries.Count, Is.GreaterThan(Load("pam-simple.json").Count));
        // It does say a command wrote a path...
        Assert.That(trace.Entries.Any(e => e.CommandName == "add" && e.Path == "$.summary.totalPayoff"
            && e.Outcome == TraceOutcome.Success), Is.True);
        // ...but the record is these five members and nothing else: no source, no position, no version.
        Assert.That(typeof(TraceEntry).GetProperties().Select(p => p.Name),
            Is.EquivalentTo(new[] { "CommandName", "Path", "Outcome", "MatchedCount", "Detail" }));
        TestContext.WriteLine("native trace for '$.summary.totalPayoff': " +
            trace.Entries.First(e => e.Path == "$.summary.totalPayoff"));
        TestContext.WriteLine("native trace, writes to schedule items (first 3): " +
            string.Join(" | ", trace.Entries.Where(e => e.CommandName == "set" && e.Path.StartsWith("$.schedule")).Take(3).Select(e => $"{e.CommandName} {e.Path}")));
    }

    // ── pam-simple ───────────────────────────────────────────────────────────────

    [Test]
    public void Simple_TopLevelSummaryFields()
    {
        var script = Load("pam-simple.json");

        var total = PamLineage.Explain(script, "$.summary.totalPayoff");
        Assert.That(total.Writers, Has.Count.EqualTo(1));
        Assert.That(total.Writers[0].Writer.At, Is.EqualTo("#15"));
        Assert.That(total.Writers[0].Writer.Verb, Is.EqualTo("add"));
        Assert.That(total.Writers[0].Source, Is.EqualTo("=sum($.schedule[*].payoff)"));
        Assert.That(total.Writers[0].Writer.Guards, Is.Empty);

        var count = PamLineage.Explain(script, "$.summary.eventCount");
        Assert.That(count.Writers.Single().Writer.At, Is.EqualTo("#16"));
        Assert.That(count.Writers.Single().Source, Is.EqualTo("=count($.schedule)"));

        TestContext.WriteLine(PamLineage.Render(script, "$.summary.totalPayoff"));
    }

    [Test]
    public void Simple_IpPayoff_IsWrittenThroughAnObjectLiteralUnderThreeGuards()
    {
        var script = Load("pam-simple.json");
        var e = PamLineage.Explain(script, "$.schedule[*].payoff");

        // Three literals write "payoff" (IED, MD, IP), in script order; the last one is the IP branch.
        Assert.That(e.Writers.Where(h => h.Kind == PamLineage.Kind.Literal).Count(), Is.EqualTo(3));
        var ip = e.Writers[^1];
        Assert.That(ip.Kind, Is.EqualTo(PamLineage.Kind.Literal));
        Assert.That(ip.Writer.Target, Is.EqualTo("$.schedule[*]"), "'@' is the item of the enclosing forEach, never a resolved index");
        Assert.That(ip.Source, Does.Contain("$.state.accruedInterest").And.Contain("$.dcf"));
        Assert.That(ip.Writer.Guards, Has.Count.EqualTo(3));
        Assert.That(ip.Writer.Guards[0], Is.EqualTo("forEach $.schedule"));
        Assert.That(ip.Writer.Guards[1], Does.StartWith("else of ").And.Contain("$.initialExchangeDate"));
        Assert.That(ip.Writer.Guards[2], Does.StartWith("else of ").And.Contain("$.maturityDate"));

        TestContext.WriteLine(PamLineage.Render(script, "$.schedule[*].payoff"));
    }

    [Test]
    public void Simple_UnwrittenPathsAndRemovals()
    {
        var script = Load("pam-simple.json");

        Assert.That(PamLineage.Explain(script, "$.summary.averagePayoff").Writers, Is.Empty);
        Assert.That(PamLineage.Explain(script, "$.notionalPrincipal").Writers, Is.Empty, "an input: nothing writes it");
        Assert.That(PamLineage.Render(script, "$.summary.averagePayoff"), Does.Contain("nothing in this version writes that path"));

        var dcf = PamLineage.Explain(script, "$.dcf");
        Assert.That(dcf.Writers, Is.Not.Empty);
        Assert.That(dcf.RemovedBy.Single().Writer.At, Is.EqualTo("#" + (script.Count - 1)), "the scratch field is removed by the second-to-last command");
    }

    [Test]
    public void Simple_Dynamic_ChangingAClaimedInputChangesTheTotal_AndAnUnclaimedOneDoesNot()
    {
        var script = Load("pam-simple.json");
        var inputs = PamLineage.InputsOf(script, "$.summary.totalPayoff").Callers.ToList();
        TestContext.WriteLine("inputs of $.summary.totalPayoff: " + string.Join(", ", inputs));

        JToken Outcome(Action<JObject> change)
        {
            var c = SimpleContract();
            change(c);
            return Run("pam-simple.json", c)["summary"]!;
        }
        double Total(Action<JObject> change) => Outcome(change)["totalPayoff"]!.Value<double>();
        var baseline = Total(_ => { });

        var claimed = new (string Path, Action<JObject> Change)[]
        {
            ("$.notionalPrincipal", c => c["notionalPrincipal"] = 20000),
            ("$.nominalInterestRate", c => c["nominalInterestRate"] = 0.06),
            ("$.contractRole", c => c["contractRole"] = "RPL"),
            ("$.maturityDate", c => c["maturityDate"] = "2022-01-01"),
            ("$.dayCountConvention", c => c["dayCountConvention"] = "A365"),
        };
        foreach (var (path, change) in claimed)
        {
            Assert.That(inputs, Does.Contain(path), $"lineage should list {path} as an input");
            Assert.That(Total(change), Is.Not.EqualTo(baseline), $"changing {path} should change the total");
        }

        var unclaimed = new (string Path, Action<JObject> Change)[]
        {
            ("$.contractId", c => c["contractId"] = "OTHER"),
            ("$.currency", c => c["currency"] = "USD"),
            ("$.note", c => c["note"] = "irrelevant"),
        };
        foreach (var (path, change) in unclaimed)
        {
            Assert.That(inputs, Does.Not.Contain(path), $"lineage should not list {path}");
            Assert.That(Total(change), Is.EqualTo(baseline), $"changing {path} should not change the total");
        }
    }

    [Test]
    public void Simple_Lineage_IsMayDependNotDoesDepend()
    {
        // The payment cycle is an input of the total (it decides how many IP events there are), but on
        // A360 the interest over the whole life is the same however often it is paid, so on this
        // contract the total does not move. Lineage over-approximates, and this is the proof. The
        // event count, which the cycle does decide, does move.
        var script = Load("pam-simple.json");
        Assert.That(PamLineage.InputsOf(script, "$.summary.totalPayoff").Callers, Does.Contain("$.interestPaymentCycle.count"));
        Assert.That(PamLineage.InputsOf(script, "$.summary.eventCount").Callers, Does.Contain("$.interestPaymentCycle.count"));

        JToken Summary(int months)
        {
            var c = SimpleContract();
            c["interestPaymentCycle"]!["count"] = months;
            return Run("pam-simple.json", c)["summary"]!;
        }
        Assert.That(Summary(6)["totalPayoff"]!.Value<double>(), Is.EqualTo(Summary(3)["totalPayoff"]!.Value<double>()).Within(1e-9));
        Assert.That(Summary(6)["eventCount"]!.Value<int>(), Is.Not.EqualTo(Summary(3)["eventCount"]!.Value<int>()));
    }

    // ── pam-reference ────────────────────────────────────────────────────────────

    [Test]
    public void Reference_EventPayoffIsWrittenInsideTheItemLiteral_AfterAWholesaleWriteOfTheList()
    {
        var script = Load("pam-reference.json");
        var e = PamLineage.Explain(script, "$.events[*].payoff");

        var last = e.Writers[^1];
        Assert.That(last.Kind, Is.EqualTo(PamLineage.Kind.Literal));
        Assert.That(last.Writer.Verb, Is.EqualTo("set"));
        Assert.That(last.Writer.Target, Is.EqualTo("$.events[*]"));
        Assert.That(last.Source, Is.EqualTo("=fetch($.w.pay)"));
        Assert.That(last.Writer.Guards, Is.EqualTo(new[] { "forEach $.events" }));

        // The list itself was written earlier, as a whole, by a command whose contents are not a literal.
        var earlier = e.Writers.First();
        Assert.That(earlier.Kind, Is.EqualTo(PamLineage.Kind.Wholesale));
        Assert.That(earlier.Writer.Target, Is.EqualTo("$.events"));

        TestContext.WriteLine(PamLineage.Render(script, "$.events[*].payoff"));
    }

    [Test]
    public void Reference_RateColumn_IsConditionalOnEventType_AndTheDropMarkerIsRemoved()
    {
        var script = Load("pam-reference.json");

        // The state rate is written in four places; the last is the rate-reset branch and it clamps.
        var rate = PamLineage.Explain(script, "$.w.st.rate");
        Assert.That(rate.Writers.Count, Is.EqualTo(3));
        Assert.That(rate.Writers[^1].Source, Does.StartWith("=clamp(").And.Contain("$.w.rFloor").And.Contain("$.w.rCap"));
        Assert.That(rate.Writers[^1].Writer.Guards.Any(g => g.Contains("'RR'")), Is.True);
        Assert.That(rate.Writers.First().Writer.Guards, Is.Empty, "the initial state is unconditional");

        // A field that is written and then removed reports both, honestly.
        var drop = PamLineage.Explain(script, "$.events[*].drop");
        Assert.That(drop.Writers, Is.Not.Empty);
        Assert.That(drop.RemovedBy, Is.Not.Empty);
        Assert.That(drop.RemovedBy.Any(r => r.Writer.Guards.Count == 0), Is.True, "the unconditional remove of $.events[*].drop");
        Assert.That(drop.RemovedBy.Any(r => r.Writer.Guards.Any(g => g.StartsWith("where "))), Is.True,
            "the purchase-date filter is kept as a guard, not silently widened");

        // Nobody writes the bounds: they are inputs.
        Assert.That(PamLineage.Explain(script, "$.rateFloor").Writers, Is.Empty);
        Assert.That(PamLineage.Render(script, "$.rateFloor"), Does.Contain("nothing in this version writes that path"));

        TestContext.WriteLine(PamLineage.Render(script, "$.events[*].nominalInterestRate"));
    }

    [Test]
    public void Reference_Dynamic_TheBoundsAreInputsOfTheRateColumn_AndTheCurrencyIsNot()
    {
        var script = Load("pam-reference.json");
        var deps = PamLineage.InputsOf(script, "$.events[*].nominalInterestRate");
        var inputs = deps.Callers.ToList();
        Assert.That(deps.Defaults, Does.Contain("$.rateSpread").And.Contain("$.rateMultiplier"),
            "rateSpread and rateMultiplier have a default (add) and can be supplied");
        Assert.That(deps.Inputs, Does.Contain("$.rateFloor").And.Contain("$.rateCap"), "no default: nothing writes them");

        string Rates(Action<JObject> change)
        {
            var c = ActusPamRateBoundsTests.Contract(null, null);
            change(c);
            return string.Join(",", Run("pam-reference.json", c)["events"]!.Select(ev => ev["nominalInterestRate"]!.Value<double>().ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }
        var baseline = Rates(_ => { });

        var claimed = new (string Path, Action<JObject> Change)[]
        {
            ("$.rateFloor", c => c["rateFloor"] = 0.02),
            ("$.rateCap", c => c["rateCap"] = 0.08),
            ("$.rateSpread", c => c["rateSpread"] = 0.01),
            ("$.rateMultiplier", c => c["rateMultiplier"] = 2.0),
            ("$.nominalInterestRate", c => c["nominalInterestRate"] = 0.04),
            ("$.marketData", c => c["marketData"]![0]!["value"] = 0.03),
        };
        foreach (var (path, change) in claimed)
        {
            Assert.That(inputs, Does.Contain(path), $"lineage should list {path} as an input");
            Assert.That(Rates(change), Is.Not.EqualTo(baseline), $"changing {path} should change the rate column");
        }

        foreach (var (path, change) in new (string, Action<JObject>)[]
        {
            ("$.contractId", c => c["contractId"] = "OTHER"),
            ("$.currency", c => c["currency"] = "USD"),
        })
        {
            Assert.That(inputs, Does.Not.Contain(path));
            Assert.That(Rates(change), Is.EqualTo(baseline), $"changing {path} should not change the rate column");
        }
    }
}
