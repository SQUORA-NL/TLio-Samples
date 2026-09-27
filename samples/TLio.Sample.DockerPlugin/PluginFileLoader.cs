using System.IO.Compression;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TLio.Client;
using TLio.Core.Contracts;

namespace TLio.Sample.DockerPlugin;

/// <summary>
/// Loads and unloads TLio extension .nupkg files directly from a directory,
/// without relying on NuPlane's file-system watcher (which does not fire
/// reliably on Docker Desktop / Windows volume mounts).
/// </summary>
internal sealed class PluginFileLoader(
    MutableFunctionsProvider<JToken> functionsProvider,
    PluginCatalogService catalogService,
    ILogger<PluginFileLoader> logger)
{
    private static readonly string[] TfmPreference =
        ["net10.0", "net9.0", "net8.0", "netstandard2.1", "netstandard2.0"];

    public async Task<ReloadResult> ReloadAsync(string pluginsPath, CancellationToken ct = default)
    {
        var files = Directory.Exists(pluginsPath)
            ? Directory.GetFiles(pluginsPath, "*.nupkg")
            : [];

        var catalog = catalogService.GetCatalog();
        var loadedIds = catalog.Entries
            .Select(e => e.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fileIds = files.ToDictionary(
            f => ParsePackageId(Path.GetFileNameWithoutExtension(f)),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        var loaded = new List<string>();
        var unloaded = new List<string>();
        var errors = new List<string>();

        foreach (var id in loadedIds.Where(id => !fileIds.ContainsKey(id)))
        {
            functionsProvider.RemoveProvider(id);
            catalogService.RemoveExtension(id);
            unloaded.Add(id);
            logger.LogInformation("Plugin '{PackageId}' unloaded.", id);
        }

        foreach (var (packageId, filePath) in fileIds.Where(kv => !loadedIds.Contains(kv.Key)))
        {
            try
            {
                if (await LoadFromFileAsync(packageId, filePath, ct))
                    loaded.Add(packageId);
                else
                    errors.Add($"{packageId}: no TLio functions found");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load '{PackageId}'.", packageId);
                errors.Add($"{packageId}: {ex.Message}");
            }
        }

        return new ReloadResult(loaded, unloaded, errors);
    }

    private async Task<bool> LoadFromFileAsync(string packageId, string filePath, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(filePath);

        ZipArchiveEntry? entry = null;
        foreach (var tfm in TfmPreference)
        {
            entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.StartsWith($"lib/{tfm}/", StringComparison.OrdinalIgnoreCase) &&
                e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !e.Name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase));
            if (entry != null) break;
        }

        if (entry == null)
        {
            logger.LogWarning("No suitable DLL found in '{File}'.", Path.GetFileName(filePath));
            return false;
        }

        using var stream = entry.Open();
        var bytes = new byte[entry.Length];
        await stream.ReadExactlyAsync(bytes, ct);

        var assembly = Assembly.Load(bytes);
        var provider = new FunctionsProvider<JToken>();
        var registrarType = typeof(IFunctionsProviderRegistrar<JToken>);

        foreach (var type in assembly.GetExportedTypes())
        {
            if (!type.IsAbstract || !type.IsSealed) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1) continue;
                try
                {
                    var concrete = method.MakeGenericMethod(typeof(JToken));
                    var parameters = concrete.GetParameters();
                    if (parameters.Length != 1 || !registrarType.IsAssignableFrom(parameters[0].ParameterType)) continue;
                    concrete.Invoke(null, [provider]);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Method {Type}.{Method} skipped.", type.Name, method.Name);
                }
            }
        }

        var names = provider.GetRegisteredFunctionNames().ToArray();
        if (names.Length == 0)
        {
            logger.LogWarning("No TLio functions found in '{File}'.", Path.GetFileName(filePath));
            return false;
        }

        functionsProvider.AddProvider(packageId, provider);

        var version = ParseVersion(Path.GetFileNameWithoutExtension(filePath));
        catalogService.AddExtension(
            new LoadedExtension(packageId, assembly.GetName().Name ?? packageId, names, Guid.NewGuid()),
            new PluginPackage(packageId, version, filePath, PluginStatus.Loaded, DateTimeOffset.UtcNow, null, null));

        logger.LogInformation("Plugin '{PackageId}' v{Version} loaded — functions: {Functions}",
            packageId, version, string.Join(", ", names));
        return true;
    }

    // "tlio.extensions.math.0.1.0-preview.3" → "tlio.extensions.math"
    internal static string ParsePackageId(string nameWithoutExtension)
    {
        var parts = nameWithoutExtension.Split('.');
        var idParts = parts.TakeWhile(p => p.Length > 0 && !char.IsDigit(p[0]));
        return string.Join('.', idParts);
    }

    // "tlio.extensions.math.0.1.0-preview.3" → "0.1.0-preview.3"
    internal static string ParseVersion(string nameWithoutExtension)
    {
        var id = ParsePackageId(nameWithoutExtension);
        return nameWithoutExtension.Length > id.Length + 1
            ? nameWithoutExtension[(id.Length + 1)..]
            : "unknown";
    }
}

internal sealed record ReloadResult(List<string> Loaded, List<string> Unloaded, List<string> Errors);
