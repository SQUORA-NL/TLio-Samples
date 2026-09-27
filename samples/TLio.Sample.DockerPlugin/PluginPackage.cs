namespace TLio.Sample.DockerPlugin;

public record PluginPackage(
    string PackageId,
    string Version,
    string FilePath,
    PluginStatus Status,
    DateTimeOffset? LoadedAt,
    DateTimeOffset? UnloadedAt,
    string? ErrorMessage);
