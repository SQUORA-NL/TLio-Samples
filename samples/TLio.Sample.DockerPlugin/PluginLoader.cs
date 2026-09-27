using System.Reflection;
using Newtonsoft.Json.Linq;
using Nuplane.Abstractions;
using Nuplane.Loading;
using TLio.Client;
using TLio.Core.Contracts;

namespace TLio.Sample.DockerPlugin;

/// <summary>
/// NuPlane observer that registers/deregisters TLio extension packs when .nupkg files
/// are added to or removed from the plugins folder. Acts only as an invalidation
/// signal — authoritative state is queried from NuPlane catalogs after each event.
/// </summary>
internal sealed class PluginLoader(
    MutableFunctionsProvider<JToken> functionsProvider,
    PluginCatalogService catalogService,
    IPackageAssemblyCatalog assemblyCatalog,
    ILogger<PluginLoader> logger) : INuplaneObserver
{
    public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct) =>
        Task.CompletedTask;

    public async Task OnPackagesReconciledAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken ct)
    {
        foreach (var removed in changeSet.Removed)
        {
            functionsProvider.RemoveProvider(removed);
            catalogService.RemoveExtension(removed);
            logger.LogInformation("Plugin '{PackageId}' unloaded.", removed);
        }

        foreach (var added in changeSet.Added)
            await LoadPackageAsync(added, ct);

        foreach (var updated in changeSet.Updated)
        {
            functionsProvider.RemoveProvider(updated.Id);
            catalogService.RemoveExtension(updated.Id);
            await LoadPackageAsync(updated, ct);
        }
    }

    public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
    {
        logger.LogWarning(exception,
            "Package '{PackageId}' failed to load: {Message}", packageId, exception.Message);
        catalogService.TrackFailure(packageId, "unknown", "unknown", exception.Message);
        return Task.CompletedTask;
    }

    private async Task LoadPackageAsync(ResolvedPackage package, CancellationToken ct)
    {
        var packageAssemblies = await assemblyCatalog.GetPackagedAssembliesAsync(package.Id, ct);
        if (packageAssemblies is null)
        {
            logger.LogWarning("No assemblies found for package '{PackageId}'.", package.Id);
            return;
        }

        var provider = new FunctionsProvider<JToken>();
        var registeredFunctions = new List<string>();
        var registrarType = typeof(IFunctionsProviderRegistrar<JToken>);
        bool anyRegistered = false;

        foreach (var assembly in packageAssemblies.Assemblies)
        {
            try
            {
                if (!TryRegisterFromAssembly(assembly, provider, registrarType, package.Id))
                    continue;

                foreach (var name in provider.GetRegisteredFunctionNames())
                    if (!registeredFunctions.Contains(name, StringComparer.OrdinalIgnoreCase))
                        registeredFunctions.Add(name);

                anyRegistered = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not inspect assembly '{Assembly}' in package '{PackageId}': {Message}",
                    assembly.GetName().Name, package.Id, ex.Message);
            }
        }

        if (!anyRegistered)
        {
            // Name what was actually scanned. The usual cause of a pack landing here is not an
            // empty package but a registrar whose IFunctionsProviderRegistrar<JToken> came from
            // a different copy of TLio.Core than the host's, and without the assembly list
            // that is indistinguishable from "this package has nothing in it".
            logger.LogWarning(
                "Package '{PackageId}' contains no recognisable TLio extension assemblies — ignored. Scanned: {Assemblies}",
                package.Id,
                packageAssemblies.Assemblies.Count == 0
                    ? "(none)"
                    : string.Join(", ", packageAssemblies.Assemblies.Select(a => a.FullName)));
            return;
        }

        functionsProvider.AddProvider(package.Id, provider);

        var extension = new LoadedExtension(
            package.Id,
            packageAssemblies.Assemblies.FirstOrDefault()?.GetName().Name ?? package.Id,
            registeredFunctions.ToArray(),
            Guid.NewGuid());

        catalogService.AddExtension(extension,
            new PluginPackage(package.Id, package.Version, package.InstallPath,
                PluginStatus.Loaded, DateTimeOffset.UtcNow, null, null));

        logger.LogInformation(
            "Plugin '{PackageId}' v{Version} loaded — functions: {Functions}",
            package.Id, package.Version, string.Join(", ", registeredFunctions));
    }

    /// <summary>
    /// Scans the assembly for static classes that contain extension methods with the signature
    /// RegisterXxx&lt;TNode&gt;(this IFunctionsProviderRegistrar&lt;TNode&gt;) and calls them to
    /// populate <paramref name="provider"/>.
    /// </summary>
    private bool TryRegisterFromAssembly(
        Assembly assembly,
        FunctionsProvider<JToken> provider,
        Type registrarType,
        string packageId)
    {
        bool found = false;

        foreach (var type in assembly.GetExportedTypes())
        {
            if (!type.IsAbstract || !type.IsSealed) continue; // static class only

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!method.IsGenericMethodDefinition) continue;
                if (method.GetGenericArguments().Length != 1) continue;

                try
                {
                    var concrete = method.MakeGenericMethod(typeof(JToken));
                    var parameters = concrete.GetParameters();
                    if (parameters.Length != 1) continue;
                    if (!registrarType.IsAssignableFrom(parameters[0].ParameterType))
                    {
                        // A registrar-shaped method whose parameter is nonetheless not our
                        // registrar means two copies of TLio.Core are in play — the pack's load
                        // context resolved its own instead of deferring to the host's. Say which
                        // two, because the type names are identical and the log is otherwise
                        // baffling.
                        if (parameters[0].ParameterType.Name == registrarType.Name)
                            logger.LogWarning(
                                "'{Type}.{Method}' in '{PackageId}' takes {Param} from {ParamAsm}, but the host's is from {HostAsm} — the package resolved its own copy instead of sharing the host's.",
                                type.Name, method.Name, packageId,
                                parameters[0].ParameterType.Name,
                                parameters[0].ParameterType.Assembly.FullName,
                                registrarType.Assembly.FullName);
                        continue;
                    }

                    concrete.Invoke(null, [provider]);
                    found = true;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex,
                        "Method {Type}.{Method} in '{PackageId}' skipped: {Message}",
                        type.Name, method.Name, packageId, ex.Message);
                }
            }
        }

        return found;
    }
}
