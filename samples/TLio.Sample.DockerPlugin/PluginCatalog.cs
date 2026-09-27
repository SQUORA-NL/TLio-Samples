namespace TLio.Sample.DockerPlugin;

public record PluginCatalog(
    IReadOnlyList<LoadedExtension> Entries,
    DateTimeOffset LastUpdated);
