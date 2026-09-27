using System.Text.Json;
using System.Xml.Linq;
using YamlDotNet.Serialization;

namespace TLio.Sample.Api.Services;

internal sealed class RegistrationPayloadParser
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public (string Slug, string Script)? Parse(string body, string? contentType)
    {
        if (contentType is not null)
        {
            if (contentType.Contains("application/json") || contentType.Contains("text/json"))
                return TryJson(body);
            if (contentType.Contains("application/xml") || contentType.Contains("text/xml"))
                return TryXml(body);
            if (contentType.Contains("application/yaml") || contentType.Contains("text/yaml"))
                return TryYaml(body);
        }

        return TryJson(body) ?? TryXml(body) ?? TryYaml(body);
    }

    private static (string Slug, string Script)? TryJson(string body)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<RegistrationDto>(body, JsonOpts);
            if (dto?.Slug is null || dto.Script is null) return null;
            return (dto.Slug, dto.Script);
        }
        catch { return null; }
    }

    private static (string Slug, string Script)? TryXml(string body)
    {
        try
        {
            var doc = XDocument.Parse(body);
            var slug = doc.Root?.Element("slug")?.Value;
            var script = doc.Root?.Element("script")?.Value;
            if (slug is null || script is null) return null;
            return (slug, script);
        }
        catch { return null; }
    }

    private static (string Slug, string Script)? TryYaml(string body)
    {
        try
        {
            var deserializer = new DeserializerBuilder().Build();
            var dto = deserializer.Deserialize<RegistrationDto>(body);
            if (dto?.Slug is null || dto.Script is null) return null;
            return (dto.Slug, dto.Script);
        }
        catch { return null; }
    }

    private sealed class RegistrationDto
    {
        public string? Slug { get; set; }
        public string? Script { get; set; }
    }
}
