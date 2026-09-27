# TLio.Sample.Api

A minimal ASP.NET Core API that demonstrates TLio transformations over HTTP.

## Prerequisites

- .NET 10 SDK
- Nothing else — TLio is restored from NuGet (see [Which TLio](../../README.md#which-tlio))

## Running

```sh
dotnet run --project samples/TLio.Sample.Api
```

The API starts on `http://localhost:5100`.

## Endpoints

| Method | Route | Content-Type (request + response) |
|--------|-------|------------------------------------|
| POST   | `/transform/json` | `application/json` |
| POST   | `/transform/xml`  | `application/xml`  |
| POST   | `/transform/yaml` | `text/yaml`        |

## Try it: JSON

```sh
curl -s -X POST http://localhost:5100/transform/json \
     -H "Content-Type: application/json" \
     -d '{"name":"Alice","age":30}'
```

Response (200 OK, `Content-Type: application/json`):
```json
{"name":"Alice","age":30,"greeting":"Hello from TLio!"}
```

## Try it: XML

```sh
curl -s -X POST http://localhost:5100/transform/xml \
     -H "Content-Type: application/xml" \
     -d '<person><name>Alice</name><age>30</age></person>'
```

Response (200 OK, `Content-Type: application/xml`):
```xml
<person><name>Alice</name><age>30</age><greeting>Hello from TLio!</greeting></person>
```

## Try it: YAML

```sh
curl -s -X POST http://localhost:5100/transform/yaml \
     -H "Content-Type: text/yaml" \
     -d 'name: Alice'
```

Response (200 OK, `Content-Type: text/yaml`):
```yaml
name: Alice
greeting: Hello from TLio!
```

## Error cases

```sh
# 400 — empty body
curl -s -o /dev/null -w "%{http_code}" -X POST http://localhost:5100/transform/json \
     -H "Content-Type: application/json" -d ''

# 422 — transformation failed (engine error)
# The response body includes { "error": "...", "log": [...] }
```

## What the bundled scripts do

Each script adds a `greeting` property to any input document:

| Script file | Operation |
|-------------|-----------|
| `Scripts/transform-json.json` | Adds `$.greeting = "Hello from TLio!"` |
| `Scripts/transform-xml.json`  | Adds `<greeting>Hello from TLio!</greeting>` as a child of the root element |
| `Scripts/transform-yaml.json` | Adds `greeting: Hello from TLio!` |

## Replacing the bundled scripts

Edit or replace the JSON files in `Scripts/` and restart the server.
Each script is a JSON array of TLio command objects. See [`docs/samples`](../../docs/samples/) for worked examples.
