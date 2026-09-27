using Newtonsoft.Json.Linq;
using TLio.Client;
using TLio.Extensions.Looping;
using TLio.Extensions.Math;
using TLio.Extensions.Text;
using TLio.Extensions.TimeDate;
using TLio.Json;

namespace TLio.Sample.Actus.Api;

/// <summary>
/// Builds the engine both PAM endpoints run against. Pure JSON in, JSON out — no format
/// boundary to cross, so unlike the AFD sample this needs no <c>MultiFormatScriptRunner</c>,
/// just a plain <see cref="ScriptEngine{TNode}"/>.
/// </summary>
internal static class EngineSetup
{
    public static ScriptEngine<JToken> CreateEngine()
    {
        var options = ParseOptions<JToken>.CreateDefault();
        options.FunctionsProvider.RegisterMath<JToken>();
        options.FunctionsProvider.RegisterText<JToken>();
        options.FunctionsProvider.RegisterTimeDate<JToken>();
        options.CommandsProvider.RegisterLooping<JToken>();
        return new ScriptEngine<JToken>(options.CommandsProvider, options.FunctionsProvider);
    }
}
