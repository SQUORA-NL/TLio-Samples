namespace TLio.Sample.DockerPlugin;

public record LoadedExtension(
    string PackageId,
    string AssemblyName,
    string[] Functions,
    Guid LoadContextId);
