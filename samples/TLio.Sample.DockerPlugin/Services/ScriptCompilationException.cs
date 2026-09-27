namespace TLio.Sample.DockerPlugin.Services;

internal sealed class ScriptCompilationException(string detail)
    : Exception($"Script compilation failed: {detail}");
