# Showcase scripts and their start objects

This folder holds one showcase script per format, each executing **every registered command**
(15: `add`, `set`, `put`, `remove`, `copy`, `move`, `rename`, `merge`, `compare`,
`decisionTable`, `ifElse`, `flatten`, `restore`, `resolve`, `tocsv`) and **every registered
function** (98 names across the core, Text, Math and TimeDate packs — 97 implementations, since
`path` is an alias for `scriptPath`) against a start document.
The three start documents are the *same* document, written in each format's canonical shape
(see `docs/ai-ref/adapters/document-shape.md`), and every script produces the equivalent
result — that parity is what [`TLio.Parity.Tests`](https://github.com/SQUORA-NL/TLio/tree/main/TLio.Parity.Tests) in the TLio repository guards.

| Format | Script | Start object | Path language |
|---|---|---|---|
| JSON | `showcase-json.json` | `start.json` | JSONPath — `$.people[0].score` |
| YAML | `showcase-yaml.json` | `start.yaml` | dot notation, shared with JSON — `$.people[0].score` |
| XML  | `showcase-xml.json`  | `start.xml`  | XPath — `/showcase/people/item[1]/score` (1-based) |

All three scripts are written in the JSON command notation, which every entry point (CLI,
API, MCP) accepts for every format; only the paths speak each format's own language. The
JSON and YAML scripts are therefore identical — the two formats share a path language, which
is the point: the same script text drives both. XML and YAML also have native script
notations (`<script><set path="…">…</script>` and YAML mappings); the reference spellings
for those live in [`TLio.Parity.Tests/Sweep/sweep.xml`](https://github.com/SQUORA-NL/TLio/blob/main/TLio.Parity.Tests/Sweep/sweep.xml) in the TLio repository and the notation docs.

Run them with the CLI sample (format is detected from the input file's extension):

```sh
dotnet run --project samples/TLio.Sample.Cli -- --input docs/showcase/start.json --script docs/showcase/showcase-json.json
dotnet run --project samples/TLio.Sample.Cli -- --input docs/showcase/start.yaml --script docs/showcase/showcase-yaml.json
dotnet run --project samples/TLio.Sample.Cli -- --input docs/showcase/start.xml  --script docs/showcase/showcase-xml.json
```

## What the start object contains

Each field exists to give some command or function something real to work on:

| Field | Used by |
|---|---|
| `name`, `padded` | `set`/`put`/`copy`/`move`/`rename`; text functions (`concat`, `trim`, `replace`, …) |
| `numbers` | aggregates (`sum`, `avg`, `min`, `max`, `median`, `count`) |
| `tags` | `join`, `in`, `matches`, `isArray`, the `ifElse` conditions |
| `people` | conditional aggregates (`sumif` family), comparisons, `compare`, `=partial()` |
| `nested.deep.value` | `isObject`, `=promote()` |
| `flagText`, `emptyField` | `isBoolean`, `isNull` (an explicit null / empty element / bare key) |
| `dates` | date functions (`dateCompare`, `isDateBetween`, `minDate`, `maxDate`, `avgDate`) |
| `ref` | `=indirect()` — holds a *path as data*, so it is the one field whose value differs per format (`$.name` vs `/showcase/name`) |
| `mergeSource`/`mergeTarget`, `arrSource`/`arrTarget` | `merge` (object merge and `arrayMergeMode: concat`) |
| `etl.src`, `etl.rows`, `etl.resolveRef` + `etl.lookup` | `flatten`, `tocsv`, `resolve` |
| `dt.status` | `decisionTable` |
| `trip.user` | the `flatten` → `restore` round trip |

## The start object, per format

### JSON — `start.json`

```json
{
  "name": "Ada Lovelace",
  "padded": "  spaced  ",
  "numbers": [3, 1, 4],
  "tags": ["alpha", "beta"],
  "people": [
    { "id": "p1", "score": 10 },
    { "id": "p2", "score": 20 }
  ],
  "nested": { "deep": { "value": "buried" } },
  "flagText": "true",
  "emptyField": null,
  "dates": ["2024-03-01", "2024-01-15", "2024-02-01"],
  "ref": "$.name",
  "mergeSource": { "extra": "merged-in" },
  "mergeTarget": { "kept": "still-here" },
  "arrSource": { "list": ["s1"] },
  "arrTarget": { "list": ["t1"] },
  "etl": {
    "src": { "user": { "name": "Alice", "city": "Amsterdam" } },
    "rows": [
      { "id": "r1", "qty": 2 },
      { "id": "r2", "qty": 5 }
    ],
    "resolveRef": { "code": "A" },
    "lookup": [
      { "code": "A", "label": "Alpha" }
    ]
  },
  "dt": { "status": "active" },
  "trip": { "user": { "name": "Alice", "city": "Amsterdam" } }
}
```

### YAML — `start.yaml`

The same document in YAML. `emptyField: null` could equally be written as a bare
`emptyField:` — a plain empty scalar reads as null, exactly like the empty XML element
(a quoted `''` stays an empty string).

```yaml
name: Ada Lovelace
padded: '  spaced  '
numbers:
  - 3
  - 1
  - 4
tags:
  - alpha
  - beta
people:
  - id: p1
    score: 10
  - id: p2
    score: 20
nested:
  deep:
    value: buried
flagText: 'true'
emptyField: null
dates:
  - 2024-03-01
  - 2024-01-15
  - 2024-02-01
ref: $.name
mergeSource:
  extra: merged-in
mergeTarget:
  kept: still-here
arrSource:
  list:
    - s1
arrTarget:
  list:
    - t1
etl:
  src:
    user:
      name: Alice
      city: Amsterdam
  rows:
    - id: r1
      qty: 2
    - id: r2
      qty: 5
  resolveRef:
    code: A
  lookup:
    - code: A
      label: Alpha
dt:
  status: active
trip:
  user:
    name: Alice
    city: Amsterdam
```

### XML — `start.xml`

The same document in the canonical XML shape: the document element (`<showcase>`) is named
in every path, an array is an element whose children share one name (`<item>`), and
`<emptyField/>` is the null. A single-item array keeps the canonical item name `item` so it
still reads as an array.

```xml
<showcase>
  <name>Ada Lovelace</name>
  <padded>  spaced  </padded>
  <numbers>
    <item>3</item>
    <item>1</item>
    <item>4</item>
  </numbers>
  <tags>
    <item>alpha</item>
    <item>beta</item>
  </tags>
  <people>
    <item>
      <id>p1</id>
      <score>10</score>
    </item>
    <item>
      <id>p2</id>
      <score>20</score>
    </item>
  </people>
  <nested>
    <deep>
      <value>buried</value>
    </deep>
  </nested>
  <flagText>true</flagText>
  <emptyField/>
  <dates>
    <item>2024-03-01</item>
    <item>2024-01-15</item>
    <item>2024-02-01</item>
  </dates>
  <ref>/showcase/name</ref>
  <mergeSource>
    <extra>merged-in</extra>
  </mergeSource>
  <mergeTarget>
    <kept>still-here</kept>
  </mergeTarget>
  <arrSource>
    <list>
      <item>s1</item>
    </list>
  </arrSource>
  <arrTarget>
    <list>
      <item>t1</item>
    </list>
  </arrTarget>
  <etl>
    <src>
      <user>
        <name>Alice</name>
        <city>Amsterdam</city>
      </user>
    </src>
    <rows>
      <item>
        <id>r1</id>
        <qty>2</qty>
      </item>
      <item>
        <id>r2</id>
        <qty>5</qty>
      </item>
    </rows>
    <resolveRef>
      <code>A</code>
    </resolveRef>
    <lookup>
      <item>
        <code>A</code>
        <label>Alpha</label>
      </item>
    </lookup>
  </etl>
  <dt>
    <status>active</status>
  </dt>
  <trip>
    <user>
      <name>Alice</name>
      <city>Amsterdam</city>
    </user>
  </trip>
</showcase>
```

## Format-specific notes

- **Relative paths in settings** differ with the path language: `resolve` keys and
  `decisionTable` input/output paths are `@.code` in JSON/YAML and `./code` in XML.
- **`ref`** holds a path *as data* for `=indirect()`, so its value is spelled in each
  format's own path language — the one deliberate difference between the start objects.
- **Null spellings**: JSON `null`, YAML bare `key:` (or `null`/`~`), XML `<emptyField/>`.
  All three read as null *and* as an unfilled container that `add`/`put` may write into —
  see `docs/behaviour-decisions.md` for the empty-element ambiguities XML cannot avoid.
- The scripts run warning-free against these start objects on every format (including both
  XML fetchers, slash-path and native XPath — the showcase's XPath subset is valid in both).
