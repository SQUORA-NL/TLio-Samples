using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ActusOracle;

/// <summary>Deterministic xorshift64 (no System.Random dependency, stable across runtimes).</summary>
public sealed class Rng
{
    private ulong _s;
    public Rng(ulong seed) { _s = seed == 0 ? 1 : seed; }
    public ulong Next() { _s ^= _s << 13; _s ^= _s >> 7; _s ^= _s << 17; return _s; }
    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
    public int Int(int min, int maxInclusive) => min + (int)(Next() % (ulong)(maxInclusive - min + 1));
    public bool Chance(double p) => NextDouble() < p;
    public T Pick<T>(IReadOnlyList<T> xs) => xs[Int(0, xs.Count - 1)];
}

public static class J
{
    public static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false,
    };

    public static string Inv(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    public static string Iso(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Compact JSON, but lists nested up to <paramref name="lineDepth"/> levels get one element per line.</summary>
    public static string Emit(object? value, int lineDepth = 2, int depth = 0)
    {
        if (value is System.Collections.IEnumerable e and not string and not System.Collections.IDictionary
            && depth < lineDepth && value is not double[] && value is not float[] && value is not int[])
        {
            var parts = new List<string>();
            foreach (var x in e) parts.Add(Emit(x, lineDepth, depth + 1));
            return "[\n" + string.Join(",\n", parts) + "\n]";
        }
        if (value is System.Collections.IDictionary d && depth < lineDepth)
        {
            var sb = new StringBuilder("{");
            var first = true;
            foreach (System.Collections.DictionaryEntry kv in d)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append(JsonSerializer.Serialize(kv.Key.ToString(), Options)).Append(':').Append(Emit(kv.Value, lineDepth, depth + 1));
            }
            return sb.Append('}').ToString();
        }
        return JsonSerializer.Serialize(value, Options);
    }

    public static void WriteFile(string path, string generator, string acceleratorOrPackageVersion,
        Dictionary<string, object?> extraHeader, List<object?> cases, int lineDepth = 2)
    {
        var doc = new Dictionary<string, object?>
        {
            ["generator"] = generator,
            ["acceleratorOrPackageVersion"] = acceleratorOrPackageVersion,
        };
        foreach (var kv in extraHeader) doc[kv.Key] = kv.Value;
        doc["cases"] = cases;
        // Header keys stay on one line each; "cases" gets one entry per line.
        File.WriteAllText(path, Emit(doc, lineDepth), new UTF8Encoding(false));
    }
}
