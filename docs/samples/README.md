# Samples

Ten end-to-end samples. Each is a directory holding an **input**, a **script** and the
**output** the script produces — committed, byte for byte, so a change that moves a number is
visible in a diff.

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/<sample>/input.<ext> \
  --script docs/samples/<sample>/script.<ext>
```

The CLI prints compact output; the committed file is the same document, indented. Every script
is deterministic — dates come from a field in the message rather than from `=datetime()` — so a
run tomorrow still reproduces the committed output.

The CLI registers the Math, Text and TimeDate function packs and the ETL command pack
(`resolve`); a host that only calls `ParseOptions.CreateDefault()` will not resolve.

## The samples

| Sample | Data | Script | What it is |
|---|---|---|---|
| [`car-insurance-nl/native`](car-insurance-nl/) | JSON | JSON | Full motor rating on an insurer's own message shape |
| [`car-insurance-nl/sivi-afd`](car-insurance-nl/) | JSON | JSON | The same rating on a SIVI AFD 2.0-style message |
| [`home-insurance-nl/sivi-afd-xml`](home-insurance-nl/) | **XML** | **XML** | Woonhuis + inboedel package, rated in XPath |
| [`life-insurance-nl/sivi-afd-xml`](life-insurance-nl/) | **XML** | **XML** | Overlijdensrisicoverzekering, two-key mortality join |
| [`sivi-afd1-to-afd2`](sivi-afd1-to-afd2/) | XML → **JSON** | JSON | AFD 1.0 flat records migrated to the AFD 2.0 entity model |
| [`bromfiets-nl/gim`](bromfiets-nl/) | JSON | JSON | Moped insurance rated and accepted from a GIM rekenkern's own calculation and validation sheets |
| [`ecommerce-order-fulfilment`](ecommerce-order-fulfilment/) | JSON | JSON | Webshop order to warehouse pick list |
| [`healthcare-lab-result`](healthcare-lab-result/) | XML → **JSON** | JSON | HL7 v2 lab results to a FHIR-shaped bundle |
| [`logistics-shipment`](logistics-shipment/) | **YAML** | **YAML** | Shipment booking to carrier manifest and customs regime |
| [`customer-record-normalization`](customer-record-normalization/) | JSON | JSON | CRM export normalized so every nested entity is array-shaped, `setProperties` + `=scriptpath` find mode |

The five insurance samples are deliberately one product family seen five ways. The next three
are there to show that none of this is insurance machinery — the same eight commands map an
order, a lab result and a freight booking. The last one is a single feature worked through on
its own, small enough to read start to finish in one sitting.

## The samples chain

`sivi-afd1-to-afd2` produces, byte for byte, the `input.json` that `car-insurance-nl/sivi-afd`
consumes. Running the two back to back reproduces that sample's committed `output.json`
exactly:

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/sivi-afd1-to-afd2/input.xml \
  --script docs/samples/sivi-afd1-to-afd2/script.json \
  --output /tmp/afd2.json

dotnet run --project samples/TLio.Sample.Cli -- \
  --input  /tmp/afd2.json \
  --script docs/samples/car-insurance-nl/sivi-afd/script.json
```

A legacy flat file goes in one end and a priced, accepted policy comes out the other, through
two scripts that know nothing about each other.

---

## The one thing worth reading these for

**Each format's path language decides which commands you need.** The same lookup — join a rate
row to a request field — is a different shape in each, and none of the three is a workaround.

### XML: the predicate does the join

An XPath predicate can compare against another absolute path, so the join lives in the path and
no lookup command is involved:

```xml
<put path="/afdMessage/calc/regionFactor"
     value="=fetch(/afdMessage/rates/postcodeZones/item[pc4 = /afdMessage/calc/pc4]/regionFactor)"/>
```

It composes straight into arithmetic, and a two-column key is still one step — which is what the
life sample's mortality table needs:

```xml
value="=fetch(/afdMessage/rates/mortalityRates/item[ageBand = /afdMessage/calc/ageBand
              and gender = /afdMessage/policy/parties/party[1]/genderCode]/ratePerMille)"
```

XPath also compares two node-sets as "any pair matches", which turns a set intersection into a
path. This is the home sample's options total — every tariff row whose code was requested:

```xml
value="=sum(/afdMessage/rates/coverageTariffs/item[code = /afdMessage/policy/coverages/coverage/coverageCode]/premiumPerMonth)"
```

### JSON and YAML: `resolve` does the join

A JSONPath filter can test an element against a **literal**, but it cannot compare it to another
part of the document. So the JSON side needs a lookup command — and `resolve` is that command,
including its per-element form, which the XML samples have no use for:

```json
{ "command": "resolve", "path": "$.order.lines[*]",
  "resolveSettings": [ {
    "referencesCollectionPath": "$.ref.pickLocations[*]",
    "resolveKeys": [ { "keyPath": "@.sku", "referenceKeyPath": "@.sku" } ],
    "values":     [ { "targetPath": "@.pickLocation", "value": "@.location" } ] } ] }
```

`resolve` works in every format, and its `@.field` is written the same way in all of them —
`@.label`, never `./label`, even in XML. The marker belongs to the script, not to the document:
all four places that take it read it off the node with the adapter, because the matched entry is
not something a path can name. (In XML it *did* go through the path language, and wrote nothing;
`Resolve.TryReadFromMatch` is where that is now decided.)

Which one to reach for is a real choice, not a workaround either way: the predicate keeps the
join in the path and composes into arithmetic, `resolve` handles a whole array in one command.

---

## Shared conventions

**The rate book travels with the script.** One `add` at the top, one `remove` at the bottom.
Everything between only looks things up in it, so adding a postcode, a roof type or an HS code is
a data edit rather than a script change. In a service you would `add` it from your own store and
nothing below that command would change.

**`title` and `description` on every command.** A JSON script has no comments, so they are
properties on `CommandBase` — nothing reads them during execution. The convention here: **the
title says what the step is, the description says why it is written that way.** In XML notation
they are attributes only, because a `<title>` child element would be part of the value.

**A band is a table; a ladder is arithmetic.** Ranges (`">=24 && <30"`) stay `decisionTable`
rules because a range is rule text. A value that is really one sum written out twenty times
becomes one `=clamp`. The samples do both, in that spirit.

**`=if` for a value, `ifElse` for a node.** `=if` picks between two or three *values* and
evaluates only the branch it takes, so arithmetic for a cover that was not granted never runs.
`ifElse` is for a field that may not exist at all — every policy clause in these samples is one.

**One object-valued `put` per entity.** `=fetch(...)` inside an object value is evaluated as the
object is written, at any depth — so a whole entity, or in the AFD migration a whole message, is
one command that reads like a mapping table.

## Constraints these samples are shaped around

- **Function arguments resolve against the document root.** `@.field` inside a function call does
  not mean "this array element", so **per-element arithmetic is not available**. Per-line values
  come from a lookup (`resolve`), and everything computed is computed once on scalar fields.
  The e-commerce sample takes its line totals from the order for exactly this reason.
- **`=sum()` over an empty match fails.** Guard an optional collection with `=if` on a `=count`,
  as the life sample's rider surcharge does.
- **A path ending in a selector addresses the nodes it matches.** `put` on
  `coverage[coverageCode='X']` or `lines[?(@.sku=='X')]` writes to each match and leaves the
  siblings alone; on `items[*]` it writes to every element. (It used to split the selector off as
  a property name, which left the collection standing in as the parent and replaced every sibling
  with the one value — `IItemsFetcher.IsLeafNodeSelector` is what routes it now.) A selector that
  matches nothing is a no-op with a warning: there is no single structure to scaffold.
- **A filter cannot be followed by an index.** `$.RECORDS[?(@.SRT=='04')][0]` does not resolve,
  and `copy` will not take a filtered `fromPath`. The AFD migration copies the whole record array
  and filters it down with `remove`, which does take a filter.
- **`convert` works from any notation**, and each section comes back in the notation it was
  written in. What a host must do is give the engines behind its section executors that notation —
  `UseXmlScripts()` / `UseYamlScripts()` — or the section parses to nothing and the executor says
  so. Both converting samples here are written in JSON notation with XPath paths before the
  boundary, which is worth seeing on its own: **the notation is independent of the format of the
  data**, and only the paths change at a boundary.
- **An unresolved `=fetch` writes its own text.** A path that matches nothing leaves the
  expression string in the document rather than failing the run. It is worth grepping an output
  for `=fetch(` before trusting it.

## Conversion notes

Converting XML to JSON has two asymmetries that no setting resolves:

- **XML always has exactly one root element**, so the JSON always has exactly one top-level key.
  The healthcare sample `move`s it away to get FHIR's fields to the top.
- **XML carries no types.** `inferTypes: true` makes `78` a number, which FHIR's
  `valueQuantity.value` requires — and makes an all-digit identifier a number too, which it does
  not. The healthcare sample takes that route and puts the BSN back as text in one command; the
  AFD migration takes the other route, converting first with `inferTypes` off and then typing each
  field deliberately with `=divide(x, 100)` and `=divide(x, 1)`. **The second route is usually the
  better one** — it is the one that gives cents-to-euros and leading-zero codes for free.

## About the SIVI samples

`car-insurance-nl/sivi-afd`, `home-insurance-nl` and `life-insurance-nl` are **shaped after**
SIVI AFD 2.0 / AFS: an envelope with a message header, a `policy` entity holding `parties`,
`objects`, `coverages` and `premium`, each tagged with `entityType`, and coded values. The
attribute names follow that style but are an **illustrative subset, not a certified AFD schema** —
`riskProfile` and `acceptance` in particular are insurer extensions, not AFD entities.

`sivi-afd1-to-afd2` is likewise shaped after AFD 1.0's flat, label-coded record layout rather
than transcribed from the dictionary. Map the names onto your own AFD dictionary before using any
of this against a real trading partner; only the paths change when you do.

The rating models, rate books and acceptance rules are illustrative. They are internally
consistent and the arithmetic is checked, but they are not any insurer's actual tariff.
