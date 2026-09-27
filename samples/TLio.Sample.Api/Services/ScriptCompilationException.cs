namespace TLio.Sample.Api.Services;

internal sealed class ScriptCompilationException(string detail)
    : Exception($"Script compilation failed: {detail}");
