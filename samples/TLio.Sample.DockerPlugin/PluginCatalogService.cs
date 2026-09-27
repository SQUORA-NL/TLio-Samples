namespace TLio.Sample.DockerPlugin;

public sealed class PluginCatalogService
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly List<LoadedExtension> _extensions = new();
    private readonly Dictionary<string, PluginPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastUpdated = DateTimeOffset.UtcNow;

    public void AddExtension(LoadedExtension extension, PluginPackage package)
    {
        _lock.EnterWriteLock();
        try
        {
            _extensions.RemoveAll(e => e.PackageId.Equals(extension.PackageId, StringComparison.OrdinalIgnoreCase));
            _extensions.Add(extension);
            _packages[package.PackageId] = package;
            _lastUpdated = DateTimeOffset.UtcNow;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void RemoveExtension(string packageId)
    {
        _lock.EnterWriteLock();
        try
        {
            _extensions.RemoveAll(e => e.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            if (_packages.TryGetValue(packageId, out var pkg))
                _packages[packageId] = pkg with { Status = PluginStatus.Unloaded, UnloadedAt = DateTimeOffset.UtcNow };
            _lastUpdated = DateTimeOffset.UtcNow;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void TrackFailure(string packageId, string version, string filePath, string errorMessage)
    {
        _lock.EnterWriteLock();
        try
        {
            _packages[packageId] = new PluginPackage(
                packageId, version, filePath,
                PluginStatus.Failed, null, null, errorMessage);
            _lastUpdated = DateTimeOffset.UtcNow;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public PluginCatalog GetCatalog()
    {
        _lock.EnterReadLock();
        try { return new PluginCatalog(_extensions.ToList(), _lastUpdated); }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<PluginPackage> GetAllPackages()
    {
        _lock.EnterReadLock();
        try { return _packages.Values.ToList(); }
        finally { _lock.ExitReadLock(); }
    }
}
