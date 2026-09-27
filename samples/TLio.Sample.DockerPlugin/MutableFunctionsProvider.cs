using TLio.Client;
using TLio.Core.Contracts;

namespace TLio.Sample.DockerPlugin;

/// <summary>
/// Thread-safe IFunctionsProvider wrapper that allows plugin packs to be added and removed
/// at runtime without restarting the host.
/// Last-registered pack wins when two packs register the same function name.
/// </summary>
public sealed class MutableFunctionsProvider<TNode> : IFunctionsProvider<TNode>, IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly FunctionsProvider<TNode> _builtins;
    private readonly Dictionary<string, FunctionsProvider<TNode>> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MutableFunctionsProvider<TNode>> _logger;

    public MutableFunctionsProvider(
        FunctionsProvider<TNode> builtins,
        ILogger<MutableFunctionsProvider<TNode>> logger)
    {
        _builtins = builtins;
        _logger   = logger;
    }

    public void AddProvider(string packageId, FunctionsProvider<TNode> provider)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_packs.ContainsKey(packageId))
                _logger.LogWarning("Package '{PackageId}' is already registered — replacing existing provider.", packageId);

            foreach (var name in provider.GetRegisteredFunctionNames())
            {
                var alreadyExists = _packs.Values.Any(p => p.GetFunction(name) != null)
                                    || _builtins.GetFunction(name) != null;
                if (alreadyExists)
                    _logger.LogWarning(
                        "Function '{Name}' from package '{PackageId}' conflicts with an existing registration — new pack wins.",
                        name, packageId);
            }

            _packs[packageId] = provider;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void RemoveProvider(string packageId)
    {
        _lock.EnterWriteLock();
        try { _packs.Remove(packageId); }
        finally { _lock.ExitWriteLock(); }
    }

    public IFunction<TNode>? GetFunction(string functionName)
    {
        _lock.EnterReadLock();
        try
        {
            // Iterate in reverse insertion order so last-registered pack wins
            foreach (var provider in _packs.Values.Reverse())
            {
                var fn = provider.GetFunction(functionName);
                if (fn != null) return fn;
            }
            return _builtins.GetFunction(functionName);
        }
        finally { _lock.ExitReadLock(); }
    }

    public IEnumerable<string> GetRegisteredFunctionNames()
    {
        _lock.EnterReadLock();
        try
        {
            var names = new HashSet<string>(_builtins.GetRegisteredFunctionNames(), StringComparer.OrdinalIgnoreCase);
            foreach (var provider in _packs.Values)
                foreach (var name in provider.GetRegisteredFunctionNames())
                    names.Add(name);
            return names.ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    public IEnumerable<string> GetBuiltinFunctionNames() =>
        _builtins.GetRegisteredFunctionNames();

    public void Dispose() => _lock.Dispose();
}
