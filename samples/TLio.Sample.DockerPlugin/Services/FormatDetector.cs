using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

namespace TLio.Sample.DockerPlugin.Services;

internal sealed class FormatDetector
{
    public string? DetectFormat(string? contentType, string body)
    {
        if (contentType is not null)
        {
            if (contentType.Contains("application/json") || contentType.Contains("text/json"))
                return "json";
            if (contentType.Contains("application/xml") || contentType.Contains("text/xml"))
                return "xml";
            if (contentType.Contains("application/yaml") || contentType.Contains("text/yaml"))
                return "yaml";
        }

        if (TryJson(body)) return "json";
        if (TryXml(body)) return "xml";
        if (TryYaml(body)) return "yaml";
        return null;
    }

    private static bool TryJson(string body)
    {
        try { System.Text.Json.JsonDocument.Parse(body); return true; }
        catch { return false; }
    }

    private static bool TryXml(string body)
    {
        try { XDocument.Parse(body); return true; }
        catch { return false; }
    }

    private static bool TryYaml(string body)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(body));
            return true;
        }
        catch { return false; }
    }
}
