# TLio.Sample.DockerPlugin

A Docker-hosted TLio transformation API that hot-loads TLio extension NuGet packages at runtime. Drop a `.nupkg` into a volume-mounted folder — the API gains new functions within 10 seconds. Remove it — the functions are gone. No restart required.

Powered by [NuPlane](https://www.nuget.org/packages/Nuplane) for folder watching and assembly loading, and [CShells](https://www.nuget.org/packages/CShells) for the modular feature host.

## Prerequisites

- Docker Desktop

> **PowerShell users**: `curl` in PowerShell is an alias for `Invoke-WebRequest` and does not accept `-H`/`-d` flags. Use `curl.exe` instead — it ships with Windows 10/11 and accepts the same syntax as the commands below.

## Build and run

```bash
cd samples/TLio.Sample.DockerPlugin
mkdir -p plugins
docker build -t tlio-sample-docker-plugin -f Dockerfile ../..
docker compose up -d
```

The image restores TLio from nuget.org — the newest 1.x by default. Pin a version with
`docker build --build-arg TLIO_VERSION=1.1.0 ...` (or `TLIO_VERSION=1.1.0 docker compose build`).

## Endpoints

| Endpoint | Description |
|---|---|
| `POST /transform/{format}` | Execute a script supplied in the request (`format`: `json`, `xml`, `yaml`) |
| `POST /scripts` | Register a script under a slug |
| `POST /run/{slug}` | Execute a registered script; the input format is detected from the body |
| `GET /plugins` | List currently loaded extensions and built-in functions |
| `GET /plugins/status` | Full NuPlane load-state catalog |
| `GET /health` | Liveness probe |

### The `/transform/{format}` body

`{format}` picks the document format, and the script's **paths must speak that format's path
language** — JSONPath for `json` and `yaml`, XPath for `xml`.

```jsonc
{
  // JSON value for `json`; a string holding the document for `xml`; either for `yaml`
  // (JSON is a subset of YAML).
  "input": { "city": "Utrecht" },
  // A JSON array, or a string in JSON, XML or YAML script notation.
  "script": [ { "command": "add", "path": "$.country", "value": "NL" } ]
}
```

`data` comes back as a JSON value for `json`, and as a string holding the document for `xml`
and `yaml`:

```bash
curl -X POST http://localhost:5000/transform/xml \
  -H "Content-Type: application/json" \
  -d '{"input":"<order><city>Utrecht</city></order>","script":[{"command":"add","path":"/order/country","value":"NL"}]}'
# → {"success":true,"data":"<order><city>Utrecht</city><country>NL</country></order>"}
```

Functions from hot-loaded plugin packs are available on `json` only — packs register as
`FunctionsProvider<JToken>`, and there is no `XElement` or `YamlNode` equivalent to hand them.
`xml` and `yaml` see the built-in functions.

## Building a plugin package

A pack destined for `/plugins` must be built **without its `TLio.Core` dependency**. The pack's
sources live in the [TLio repository](https://github.com/SQUORA-NL/TLio); from a clone of it:

```bash
dotnet pack TLio.Extensions.Math/TLio.Extensions.Math.csproj -c Release \
  -o ./plugins -p:SuppressDependenciesWhenPacking=true
```

This is not a workaround, it is the contract. NuPlane resolves a package's declared NuGet
dependencies and loads the whole resulting graph into the pack's own `AssemblyLoadContext`. If
`TLio.Core` is in that graph, the pack gets its *own* copy of it, and the
`IFunctionsProviderRegistrar<JToken>` its registrar asks for is then a different type from the
host's — same name, different assembly instance — so the host recognises nothing and logs
`no recognisable TLio extension assemblies`. Leaving the dependency out keeps `TLio.Core` off the
graph, and the host supplies it instead through the shared-assembly policy in `Program.cs`.

The packages published to NuGet.org keep their normal dependencies — `dotnet add package
TLio.Extensions.Math` still brings `TLio.Core` with it. Only the plugin flavour drops it, because
only in the plugin case is the contract already in the process.

## Scenario 1: Baseline (no plugins)

```bash
curl -X POST http://localhost:5000/transform/json \
  -H "Content-Type: application/json" \
  -d '{"input":{"value":3.7},"script":[{"command":"add","path":"$.rounded","value":"=round($.value)"}]}'
# → { "success": false, "error": "Unknown function: round" }

curl http://localhost:5000/plugins
# → { "plugins": [], "builtinFunctions": [...] }
```

## Scenario 2: Add a plugin

```bash
cp path/to/TLio.Extensions.Math.*.nupkg ./plugins/
sleep 10

curl http://localhost:5000/plugins
# → plugins array now includes TLio.Extensions.Math

curl -X POST http://localhost:5000/transform/json \
  -H "Content-Type: application/json" \
  -d '{"input":{"value":3.7},"script":[{"command":"add","path":"$.rounded","value":"=round($.value)"}]}'
# → { "success": true, "data": { "value": 3.7, "rounded": 4 } }
```

## Scenario 3: Remove the plugin

```bash
rm ./plugins/TLio.Extensions.Math.*.nupkg
sleep 10

curl -X POST http://localhost:5000/transform/json \
  -H "Content-Type: application/json" \
  -d '{"input":{"value":3.7},"script":[{"command":"add","path":"$.rounded","value":"=round($.value)"}]}'
# → { "success": false, "error": "Unknown function: round" }
```

> **Not currently working.** Deleting the file triggers a reconciliation cycle, but the change
> set comes back empty (`Removed = []`) and the functions stay registered, so `round` keeps
> answering. The directory feed's role is `DesiredAndCache`, and the cache half appears to hold
> the package alive after the source file is gone — `NuplaneDirectoryFeedSetupOptions.Role` is
> where to look. Adding and updating packages (Scenarios 2 and 4) both work.

## Scenario 4: Multiple plugins

```bash
cp path/to/TLio.Extensions.Math.*.nupkg ./plugins/
cp path/to/TLio.Extensions.Text.*.nupkg ./plugins/
sleep 10

curl -X POST http://localhost:5000/transform/json \
  -H "Content-Type: application/json" \
  -d '{"input":{"price":9.999,"label":"hello"},"script":[{"command":"set","path":"$.price","value":"=round($.price)"},{"command":"set","path":"$.label","value":"=toUpper($.label)"}]}'
# → { "success": true, "data": { "price": 10, "label": "HELLO" } }
```

## Scenario 5: Invalid file — no crash

```bash
echo "not a nuget package" > ./plugins/garbage.nupkg
sleep 5

curl http://localhost:5000/health
# → { "status": "healthy" }

docker logs tlio-plugin-api 2>&1 | grep -i "warn"
# → warning about unrecognised package, no crash
```

## Conflict resolution

When two plugin packages register a function with the same name, the **last-registered** package's implementation is used. A warning is logged. To use the earlier package's version, remove the later package first.

## Stopping

```bash
docker compose down
```
