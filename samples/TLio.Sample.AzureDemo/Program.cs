using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using TLio.Sample.AzureDemo;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// The ScriptEngine and its command/function registries are built once and reused for every
// request: they hold no per-request state (that lives in the ExecutionContext created inside
// TlioTransformer.Transform), so rebuilding them per invocation would just be wasted work.
builder.Services.AddSingleton<TlioTransformer>();

builder.Build().Run();
