# TLio Samples

Runnable samples and worked examples for [TLio](https://github.com/SQUORA-NL/TLio) — the
script-driven JSON / XML / YAML transformation library.

Everything here consumes TLio the way you would: as **NuGet packages from nuget.org**. No TLio
source lives in this repository, so any sample can be lifted out and dropped into your own
solution as it is.

## Quick start

```sh
git clone https://github.com/SQUORA-NL/TLio-Samples
cd TLio-Samples
dotnet test          # restores the newest TLio, builds every sample, checks every worked example

dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/ecommerce-order-fulfilment/input.json \
  --script docs/samples/ecommerce-order-fulfilment/script.json
```

Requires the .NET 10 SDK.

## What is here

### Sample applications — `samples/`

| Project | What it shows |
|---|---|
| [`TLio.Sample.Cli`](samples/TLio.Sample.Cli/) | File in, transformed file out — JSON, XML and YAML, including mid-script `convert` |
| [`TLio.Sample.Api`](samples/TLio.Sample.Api/) | Minimal API: JSON/XML/YAML endpoints and a slug-keyed compiled-script cache |
| [`TLio.Sample.Api.IntegrationTests`](samples/TLio.Sample.Api.IntegrationTests/) | End-to-end tests of the API sample |
| [`TLio.Sample.AfdApi`](samples/TLio.Sample.AfdApi/) | SIVI AFD 1.0 / AFD Short / AFD 2.0 conversion API |
| [`TLio.Sample.Actus.Api`](samples/TLio.Sample.Actus.Api/) | ACTUS PAM contract calculator built on `forEach` / `while` |
| [`TLio.Sample.DockerPlugin`](samples/TLio.Sample.DockerPlugin/) | Docker host that hot-loads TLio extension `.nupkg` files with NuPlane |
| [`TLio.Sample.AzureDemo`](samples/TLio.Sample.AzureDemo/) | One HTTP-triggered Azure Function; the stage demo in [`demo/`](demo/) drives it |

### Worked examples — `docs/`

| Folder | What it is |
|---|---|
| [`docs/samples`](docs/samples/) | Ten end-to-end scenarios (insurance, e-commerce, HL7 → FHIR, logistics, …), each an input, a script and the committed output |
| [`docs/showcase`](docs/showcase/) | One script per format that executes every registered command |
| [`demo/`](demo/) | Azure Days demo: static page, canned scripts and `azd` infrastructure |

The reference documentation for every command and function — and the library itself — lives in
the [TLio repository](https://github.com/SQUORA-NL/TLio) (`docs/ai-ref`).

## Which TLio

`Directory.Build.props` sets one property, `TLioVersion`, and every TLio `PackageReference` uses
it. The default is the floating range `1.*-*`: each restore takes the **newest TLio 1.x on
nuget.org**, including the automatic previews published from TLio's `main`. The samples therefore
always show the library as it is today.

Pin a release when you need a reproducible build:

```sh
dotnet build -p:TLioVersion=1.1.0
```

CI builds and tests on every push and pull request, and again every night against whatever TLio
was published last — that scheduled run is what notices when a new TLio breaks a sample.

## Tests

| Project | What it checks |
|---|---|
| `tests/TLio.Samples.Tests` | Every `docs/samples` scenario is parsed, serialized back to JSON, parsed again, run — and must produce its committed output |
| `samples/TLio.Sample.Api.IntegrationTests` | The API sample, end to end |

The CI workflow also builds the DockerPlugin image and smoke-tests `/transform` in all three
formats.
