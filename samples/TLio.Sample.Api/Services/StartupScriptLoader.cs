using System.Text.Json;
using TLio.Sample.Api.Registry;

namespace TLio.Sample.Api.Services;

internal sealed class StartupScriptLoader
{
    public async Task LoadAsync(
        IScriptRegistry registry,
        ScriptCompiler compiler,
        ILogger<StartupScriptLoader> logger,
        IConfiguration config)
    {
        var configPath = config["TLIO_SCRIPTS_CONFIG"]
            ?? Environment.GetEnvironmentVariable("TLIO_SCRIPTS_CONFIG")
            ?? Path.Combine(AppContext.BaseDirectory, "scripts-config.json");

        if (!File.Exists(configPath))
        {
            logger.LogInformation("No startup script config found at {Path}", configPath);
            return;
        }

        var json = await File.ReadAllTextAsync(configPath);
        List<StartupEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<StartupEntry>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to parse startup script config at {Path}: {Error}", configPath, ex.Message);
            return;
        }

        if (entries is null) return;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Slug) || string.IsNullOrWhiteSpace(entry.Script))
            {
                logger.LogWarning("Skipping startup entry with missing slug or script");
                continue;
            }

            try
            {
                var compiled = compiler.Compile(entry.Slug, entry.Script);
                registry.Add(compiled);
                logger.LogInformation("Startup: registered slug '{Slug}'", entry.Slug);
            }
            catch (ScriptCompilationException ex)
            {
                logger.LogWarning("Startup: skipping slug '{Slug}' — {Error}", entry.Slug, ex.Message);
            }
        }
    }

    private sealed class StartupEntry
    {
        public string? Slug { get; set; }
        public string? Script { get; set; }
    }
}
