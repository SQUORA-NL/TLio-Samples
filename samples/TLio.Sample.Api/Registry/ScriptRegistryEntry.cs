using Newtonsoft.Json.Linq;
using System.Xml.Linq;
using TLio.Client;
using YamlDotNet.RepresentationModel;

namespace TLio.Sample.Api.Registry;

internal sealed record ScriptRegistryEntry(
    string Slug,
    string Source,
    CompiledScript<JToken> CompiledJson,
    CompiledScript<XElement> CompiledXml,
    CompiledScript<YamlNode> CompiledYaml,
    DateTimeOffset RegisteredAt);
