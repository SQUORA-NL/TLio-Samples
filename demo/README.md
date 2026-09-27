# TLio — Azure Days demo

A stage demo, not a product. One HTTP-triggered Azure Function (`samples/TLio.Sample.AzureDemo`)
wraps TLio's `ScriptEngine<JToken>` behind `POST /Transform`; this folder holds the static page
that drives it and the canned scripts that tell the story:

1. **Set a value** — a script is just JSON.
2. **Call a function** — `=datetime()` / `=newGuid()` run live, server-side, every request. This
   is the "JSON becomes a programming language" beat.
3. **Reshape the document** — `add` + `move` turn a flat legacy shape into a nested one.
4. **Compile once, run many** — the same script runs thousands of times two ways: parsed fresh
   every call (`ScriptEngine.Execute`) versus parsed once and reused (`ScriptEngine.Compile` +
   `CompiledScript.Execute`). The page renders both timings as bars with a speedup figure.

No auth, no queues, no database. `AuthorizationLevel.Anonymous`, one function, one page.

## Run it locally

Prerequisites: .NET 10 SDK, [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
(`npm i -g azure-functions-core-tools@4`).

```sh
cd samples/TLio.Sample.AzureDemo
dotnet run
```

`local.settings.json` sets `"AzureWebJobsStorage": "UseDevelopmentStorage=true"`, which expects
[Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) running locally.
There's nothing here that actually touches storage — it's an HTTP-only function — so if you'd
rather not run Azurite, set `AzureWebJobsStorage` to an empty string instead and Core Tools will
skip the check.

You should see:

```
Functions:
        Transform: [POST] http://localhost:7071/Transform
```

Serve the static page from `demo/` with any static file server (opening `index.html` directly
via `file://` won't work — the page `fetch()`s the example scripts, and browsers block that for
local files):

```sh
cd demo
python3 -m http.server 8080
# or: npx serve .
```

Open <http://localhost:8080>, pick an example, hit **Run**. The "Function URL" field in the
header already defaults to `http://localhost:7071/Transform`; CORS is wide open
(`"Host": { "CORS": "*" }` in `local.settings.json`) so the two different ports don't fight you.

Or skip the browser and hit it directly:

```sh
curl -s -X POST http://localhost:7071/Transform \
  -H "Content-Type: application/json" \
  -d '{"document":"{\"status\":\"pending\"}","commands":[{"command":"set","path":"$.status","value":"approved"}]}'
```

## Deploy to Azure

Either path works; pick whichever you trust more the morning of the talk.

### Option A — VS Code (simplest, no infra files involved)

1. Install the [Azure Functions extension](https://marketplace.visualstudio.com/items?itemName=ms-azuretools.vscode-azurefunctions).
2. Open `samples/TLio.Sample.AzureDemo` in VS Code.
3. Azure icon → **Workspace** → right-click **Deploy to Function App...** → follow the prompts
   to create a new Windows Function App (Consumption plan, .NET 10 isolated).
4. After deploy, open the Function App in the portal → **CORS** → add `*` (or your demo host's
   origin) so the static page can call it cross-origin.
5. Copy the function's URL (`https://<app-name>.azurewebsites.net/Transform`) into the page's
   "Function URL" field.

### Option B — `azd up`

`demo/azure.yaml` and `demo/infra/` provision a Windows **Consumption** plan Function App —
deliberately not Flex Consumption or Linux Consumption. .NET 10 isolated is supported on every
Windows and Linux hosting plan *except* Linux Consumption, so plain Windows Consumption is the
cheapest plan that still runs it; that's a demo trade-off, not a recommendation for production.
CORS (`*`) is set directly in the Bicep.

```sh
cd demo
azd auth login   # once
azd up           # provisions + deploys; prompts for environment name and region
```

`azd up` prints the function's default hostname when it finishes; the Transform endpoint is
`<that-url>/Transform`. Paste it into the page's "Function URL" field.

This has been run end-to-end against a live subscription. One thing to know: `azd up` combines
provisioning and deploy in one pass, and the deploy step can fail immediately after a fresh
provision with `unable to find a resource tagged with 'azd-service-name: transform'` — an Azure
Resource Graph propagation delay right after the Function App is created, not a Bicep problem.
If that happens, just run `azd deploy` a few seconds later; it picks up cleanly. If `azd up`
still hiccups on the day, fall back to Option A.

To tear everything down afterward:

```sh
azd down --purge
```

## The four examples

`demo/examples/*.json` each carry `{ title, narrative, document, commands, iterations? }` — the
page loads all of it, so the narration (and, for the fourth one, the benchmark) is built into the
file, not hard-coded in the page.

| File | Commands used | Beat |
|---|---|---|
| `a-simple-set.json` | `set` | A script is data. |
| `b-function-datetime.json` | `add` + `=datetime()` / `=newGuid()` | A script computes. |
| `c-copy-move.json` | `add` + `move` | A script restructures. |
| `d-precompiled-performance.json` | same script, `iterations: 8000` | A script has a cost — and compiling it away is measurable. |

Edit or add JSON files here to extend the story — the page doesn't care how many buttons it has,
it just needs `data-example` on a button in `index.html` pointing at a matching file.

### The performance example, honestly

`POST /Transform` accepts an optional `iterations` field. When it's greater than 1, the response
gains a `benchmark` object instead of running the script once, and the page shows two bars instead
of one. The speedup is real but modest — around 1.3–1.5× across every script shape tried during
development, on both a laptop and the deployed Consumption-plan Function App. Re-parsing the
script text turns out to be a real but minority share of the per-call cost; execution (path
lookups, function calls) is the rest. That's the honest number, not a cherry-picked one — the
story is "compiling avoids a real, measurable cost," not "compiling is dramatically faster."

`iterations: 8000` does **not** mean 8,000 HTTP requests. The browser sends exactly one
`POST /Transform`; `TlioTransformer.Benchmark` runs the loop server-side, inside that single
Function invocation — 8,000 calls to `ScriptEngine.Execute` (re-parses every time), then another
8,000 calls to the pre-compiled script's `Execute` — and returns only the aggregate `Stopwatch`
timings. 16,000 script executions, one round trip. That's also why the numbers are trustworthy:
network latency and the browser round trip never enter the measurement, only the parse-vs-execute
cost does.
