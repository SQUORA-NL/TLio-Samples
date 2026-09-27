# TLio-Samples Development Guidelines

Samples and worked examples for [TLio](https://github.com/SQUORA-NL/TLio). The library, its
packages and its reference documentation (`docs/ai-ref`) live in the TLio repository; this one
only *consumes* TLio.

## The one rule

TLio is referenced **only as NuGet packages** — never as a `ProjectReference`, never as copied
source. Every TLio `PackageReference` uses `Version="$(TLioVersion)"`, set once in
`Directory.Build.props` to the floating range `1.*-*` (newest 1.x on nuget.org, previews
included). Pin with `-p:TLioVersion=X.Y.Z`. Third-party packages stay pinned in their csproj.

If a sample needs a TLio change, make it in the TLio repository first, let it publish (every
push to TLio's `main` publishes a preview), then use it here.

## Layout

```text
samples/                  runnable sample applications (+ TLio.Sample.Api.IntegrationTests)
tests/TLio.Samples.Tests/ every docs/samples scenario must reproduce its committed output
docs/samples/             ten worked scenarios: input + script + committed output
docs/showcase/            one script per format exercising every command
demo/                     Azure Days stage demo driving samples/TLio.Sample.AzureDemo
```

`docs/samples` outputs are committed byte for byte. When a TLio change legitimately moves one,
re-run the scenario through `samples/TLio.Sample.Cli`, commit the new output and read the diff.

## Commands

```sh
dotnet build
dotnet test
docker build -f samples/TLio.Sample.DockerPlugin/Dockerfile -t tlio-sample-docker-plugin .
```

`global.json` pins `Azure.Functions.Sdk` for the AzureDemo, and `Directory.Solution.targets`
makes a solution-level restore generate its functions extension project (AZFW0108).
