using CShells.AspNetCore.Extensions;
using Newtonsoft.Json.Linq;
using Nuplane;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Sources.Directory.Builder;
using TLio.Client;
using TLio.Json;
using TLio.Sample.DockerPlugin;
using TLio.Sample.DockerPlugin.Endpoints;
using TLio.Sample.DockerPlugin.Registry;
using TLio.Sample.DockerPlugin.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
    o.Limits.MaxRequestBodySize = builder.Configuration.GetValue<long?>("SlugCache:MaxBodySizeBytes") ?? 10 * 1024 * 1024);

// ── TLio ─────────────────────────────────────────────────────────────────────

var builtins = new FunctionsProvider<JToken>();
var defaultOptions = ParseOptions<JToken>.CreateDefault();
foreach (var name in defaultOptions.FunctionsProvider.GetRegisteredFunctionNames())
    builtins.Register(name, () => defaultOptions.FunctionsProvider.GetFunction(name)!);

builder.Services.AddSingleton<MutableFunctionsProvider<JToken>>(sp =>
    new MutableFunctionsProvider<JToken>(
        builtins,
        sp.GetRequiredService<ILogger<MutableFunctionsProvider<JToken>>>()));

builder.Services.AddSingleton<PluginCatalogService>();
builder.Services.AddSingleton<PluginFileLoader>();

// ── Slug-cache services ───────────────────────────────────────────────────────

builder.Services.AddSingleton<IScriptRegistry, ScriptRegistry>();
builder.Services.AddSingleton<ScriptCompiler>();
builder.Services.AddSingleton<FormatDetector>();
builder.Services.AddSingleton<RegistrationPayloadParser>();
builder.Services.AddSingleton<StartupScriptLoader>();

// ── NuPlane ───────────────────────────────────────────────────────────────────

var pluginsPath = builder.Configuration["NUPLANE_PLUGINS_PATH"]
    ?? Environment.GetEnvironmentVariable("NUPLANE_PLUGINS_PATH")
    ?? "/plugins";

builder.Configuration["Nuplane:Sources:Directory:Path"] = pluginsPath;

var nuplaneConfig = builder.Configuration.GetSection("Nuplane");

builder.Services.AddNuplane(nuplaneConfig, nuplane =>
{
    nuplane.AddDirectoryFeed("plugins", pluginsPath, cfg =>
    {
        cfg.Watch = true;
        cfg.IncludeAll();
    });
    nuplane.AutoloadPackages(nuplaneConfig.GetSection("Loading"), lb =>
    {
        lb.Enable();

        // A pack is loaded into its own AssemblyLoadContext. Left alone it would resolve its
        // own copy of TLio.Core, and PluginLoader — which recognises a pack by reflecting for a
        // registrar method taking IFunctionsProviderRegistrar<JToken> — would find a type of
        // that name belonging to a *different* assembly instance, match nothing, and log the
        // pack as "no recognisable TLio extension assemblies". Naming these here makes the load
        // context defer to the host's copy, so there is one type identity on both sides.
        //
        // Newtonsoft.Json is on the list for the same reason: JToken is the TNode, so a second
        // copy of it breaks the match just as thoroughly as a second copy of TLio.Core.
        //
        // The last argument is the major version that has to match. TLio's AssemblyVersion is
        // major-only by design (1.1.0 ships as 1.0.0.0), so it is read off the TLio.Core this
        // host was restored against rather than written down — the samples float to the newest
        // TLio package, and a number written here would not follow it across a major.
        //
        // The token is TLio's strong-name key token (TlioPublicKeyToken in the TLio repository's
        // Directory.Build.props); it changes only if TLio's signing key does.
        const string TLioKey = "356c904b6d1035c0";
        var tlioMajor = typeof(TLio.Core.Contracts.INodeAdapter<>).Assembly.GetName().Version!.Major;
        lb.SharedAssembly("TLio.Core", TLioKey, tlioMajor);
        lb.SharedAssembly("TLio.Commands", TLioKey, tlioMajor);
        lb.SharedAssembly("TLio.Functions", TLioKey, tlioMajor);
        lb.SharedAssembly("TLio.Client", TLioKey, tlioMajor);
        lb.SharedAssembly("Newtonsoft.Json", "30ad4fe6b2a6aeed", 13);
    });
    nuplane.OnPackagesChanged<PluginLoader>();
});

// ── CShells ───────────────────────────────────────────────────────────────────

builder.Services.AddCShellsAspNetCore();

// ── Build ─────────────────────────────────────────────────────────────────────

var app = builder.Build();

app.MapShells();

// ── Endpoints ─────────────────────────────────────────────────────────────────

SlugExecutionEndpoints.Map(app);
ScriptManagementEndpoints.Map(app);
TransformEndpoints.Map(app);

// ── GET /plugins ──────────────────────────────────────────────────────────────

app.MapGet("/plugins", (
    PluginCatalogService catalogService,
    MutableFunctionsProvider<JToken> functionsProvider) =>
{
    var catalog = catalogService.GetCatalog();
    return Results.Ok(new
    {
        lastUpdated = catalog.LastUpdated,
        plugins = catalog.Entries.Select(e => new
        {
            packageId  = e.PackageId,
            loadedAt   = catalogService.GetAllPackages()
                             .FirstOrDefault(p => p.PackageId == e.PackageId)?.LoadedAt,
            functions  = e.Functions
        }),
        builtinFunctions = functionsProvider.GetBuiltinFunctionNames()
    });
});

// ── GET /plugins/status  (NuPlane load-state catalog) ────────────────────────

app.MapGet("/plugins/status", async (IPackageLoadStateCatalog loadStateCatalog, CancellationToken ct) =>
{
    var snapshot = await loadStateCatalog.GetLoadStateAsync(ct);
    return Results.Ok(new
    {
        entries = snapshot.Packages.Select(s => new
        {
            packageId    = s.PackageId,
            version      = s.Version,
            status       = s.Status.ToString(),
            loadedAt     = s.LoadedAtUtc,
            errorMessage = s.Diagnostics.FirstOrDefault()
        })
    });
});

// ── POST /plugins/reload ──────────────────────────────────────────────────────

app.MapPost("/plugins/reload", async (
    PluginFileLoader loader,
    IConfiguration config,
    CancellationToken ct) =>
{
    var pluginsPath = config["NUPLANE_PLUGINS_PATH"] ?? "/plugins";
    var result = await loader.ReloadAsync(pluginsPath, ct);
    return Results.Ok(new { loaded = result.Loaded, unloaded = result.Unloaded, errors = result.Errors });
});

// ── GET /health ───────────────────────────────────────────────────────────────

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// ── Startup: seed registry from config ───────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var loader  = scope.ServiceProvider.GetRequiredService<StartupScriptLoader>();
    var reg     = scope.ServiceProvider.GetRequiredService<IScriptRegistry>();
    var comp    = scope.ServiceProvider.GetRequiredService<ScriptCompiler>();
    var logger  = scope.ServiceProvider.GetRequiredService<ILogger<StartupScriptLoader>>();
    var config  = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    await loader.LoadAsync(reg, comp, logger, config);
}

app.Run();

public partial class Program { }
