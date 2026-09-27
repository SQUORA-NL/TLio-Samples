# TLio.Sample.Cli

A console application that transforms a data file using a TLio script.

## Prerequisites

- .NET 10 SDK
- Nothing else — TLio is restored from NuGet (see [Which TLio](../../README.md#which-tlio))

## Usage

```sh
dotnet run --project samples/TLio.Sample.Cli -- --input <path> --script <path> [--output <path>]
```

Or after publish:

```sh
TLio.Sample.Cli --input <path> --script <path> [--output <path>]
```

## Arguments

| Argument | Required | Description |
|----------|----------|-------------|
| `--input <path>`  | Yes | Path to the input data file (.json, .xml, .yaml, .yml) |
| `--script <path>` | Yes | Path to the TLio script file (.json) |
| `--output <path>` | No  | Write transformed output to this file (default: stdout) |
| `--help`          | No  | Show usage information and exit |

## Exit codes

| Code | Meaning |
|------|---------|
| 0  | Transformation completed successfully |
| 1  | Input file not found |
| 2  | Script file not found |
| 3  | Input file could not be parsed (format error) |
| 4  | Script file could not be parsed (invalid JSON) |
| 5  | Transformation execution failed |
| 10 | Unexpected error |

## Format auto-detection

The data format is determined from the `--input` file extension:

| Extension | Format |
|-----------|--------|
| `.json`         | JSON |
| `.xml`          | XML  |
| `.yaml`, `.yml` | YAML |

## Examples

### Transform the bundled JSON sample

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input samples/TLio.Sample.Cli/SampleInput/sample.json \
  --script samples/TLio.Sample.Cli/Scripts/transform-json.json
```

Expected output (stdout):
```json
{"name":"Alice","age":30,"greeting":"Hello from TLio!"}
```

### Save output to a file

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input samples/TLio.Sample.Cli/SampleInput/sample.json \
  --script samples/TLio.Sample.Cli/Scripts/transform-json.json \
  --output result.json
```

### Transform an XML file

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input samples/TLio.Sample.Cli/SampleInput/sample.xml \
  --script samples/TLio.Sample.Cli/Scripts/transform-xml.json
```

### Missing input file (exit code 1)

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input nonexistent.json \
  --script samples/TLio.Sample.Cli/Scripts/transform-json.json

echo "Exit code: $?"   # Prints: Exit code: 1
```

## What the bundled scripts do

Each script adds a `greeting` property to any input document:

| Script file | Operation |
|-------------|-----------|
| `Scripts/transform-json.json` | Adds `$.greeting = "Hello from TLio!"` |
| `Scripts/transform-xml.json`  | Adds `<greeting>Hello from TLio!</greeting>` as a child of the root element |
| `Scripts/transform-yaml.json` | Adds `greeting: Hello from TLio!` |

## Using your own files and scripts

1. Write a TLio script as a JSON array of command objects (see [`docs/samples`](../../docs/samples/) for worked examples).
2. Save it as `my-script.json`.
3. Run: `dotnet run --project samples/TLio.Sample.Cli -- --input my-data.json --script my-script.json`
