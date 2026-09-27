# SIVI AFD 1.0 → AFD 2.0

A migration, not a rating. A flat, label-coded **AFD 1.0** message in XML goes in; the
entity-based **AFD 2.0** message in JSON comes out.

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/sivi-afd1-to-afd2/input.xml \
  --script docs/samples/sivi-afd1-to-afd2/script.json
```

## Why this sample is worth its length

**Its output is another sample's input, byte for byte.** `output.json` is exactly the
`input.json` that [`car-insurance-nl/sivi-afd`](../car-insurance-nl/) consumes — same values,
same key order. Run the two back to back and you reproduce that sample's committed `output.json`
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

A 1990s flat file goes in one end and a priced, accepted policy comes out the other, through two
scripts that know nothing about each other.

## The two standards

**AFD 1.0** is a sequence of typed records. Every field is a numbered label, every value is text,
dates are `CCYYMMDD`, amounts are in cents with leading zeros, and what a record *means* is a
two-digit code in `SRT`:

```xml
<RECORD>
  <SRT>01</SRT>
  <AL001>OFF-2026-005177</AL001>
  <AL003>20261001</AL003>
  <AL005>02</AL005>
  <AL006>0000050000</AL006>
</RECORD>
```

**AFD 2.0 / AFS** is an entity model: a `policy` holding `parties`, `objects` and `coverages`,
each tagged with `entityType`, with mnemonic codes and native JSON types:

```json
{ "entityType": "policy",
  "quotationNumber": "OFF-2026-005177",
  "commencementDate": "2026-10-01",
  "requestedCoverageCode": "ALLRISK",
  "voluntaryExcessAmount": 500 }
```

Three things have to change at once: the **shape** (records to entities), the **vocabulary**
(`02` to `ALLRISK`), and the **types** (text to numbers, dates, nulls).

## Convert first, then type deliberately

The script's first command is the format boundary:

```json
{ "command": "convert", "to": "json", "settings": { "inferTypes": false } }
```

`inferTypes` stays **off** on purpose. AFD 1.0 is a flat file where every field is text, so
letting the converter guess would be guessing about a format that never had types. Instead each
field is typed where it is mapped, and the conversion falls out of the arithmetic:

| AFD 1.0 | Expression | AFD 2.0 |
|---|---|---|
| `0000050000` (cents) | `=divide(…AL006, 100)` | `500` |
| `02` (leading zero) | `=divide(…AL007, 1)` | `2` |
| `20261001` | `=concat(=substring(…,0,4),'-',=substring(…,4,2),'-',=substring(…,6,2))` | `"2026-10-01"` |

This is the opposite choice from the [healthcare sample](../healthcare-lab-result/), which turns
`inferTypes` on and then repairs the one field it gets wrong. **This route is usually the better
one** — it is the one that gives cents-to-euros and leading-zero codes for free.

Paths **above** the boundary are XPath; paths **below** it are JSONPath — those follow the
document. The script itself is written in JSON notation throughout, and that is worth seeing on
its own: **the notation is independent of the format of the data**, so a JSON script reads an XML
document quite happily as long as its paths are XPath. The XML and YAML notations would do this
job too; `convert` is a command like any other in all three.

## Eleven code lists, one command

Every coded field is collected into one node in its AFD 1.0 spelling, and a single `resolve`
with eleven settings translates all of them:

```json
{ "command": "resolve", "path": "$.calc.codes",
  "resolveSettings": [
    { "referencesCollectionPath": "$.map.partyRoles[*]",
      "resolveKeys": [ { "keyPath": "@.partyRole1", "referenceKeyPath": "@.afd1" } ],
      "values":     [ { "targetPath": "@.partyRole1Out", "value": "@.afd2" } ] },
    …
  ] }
```

`resolve` is needed here because a **JSONPath filter can only compare an element to a literal**,
never to another part of the document. The XML samples do the same job with a predicate and no
command at all — see the [samples index](../README.md#the-one-thing-worth-reading-these-for).

## The whole message in one `put`

`=fetch(...)` inside an object value is evaluated as the object is written, at **any** depth — so
the entire header-and-policy mapping is a single command that reads like a mapping table, with
the AFD 2.0 field on the left and the AFD 1.0 label on the right.

Records are read by filter: `$.AFDBERICHT.RECORDS[?(@.SRT=='02' && @.AL101=='01')].AL103` is
"the initials on the record that is a party and is the policyholder".

## The coverages need four commands, and why

There is one coverage record per requested cover, so the array cannot be written out by hand.
Two limitations shape the answer:

- **A filter cannot be followed by an index.** `RECORDS[?(@.SRT=='04')][0]` does not resolve.
- **`copy` will not take a filtered `fromPath`.**

So the whole record array is copied, and `remove` — which *does* take a filter — deletes
everything that is not a coverage record:

```json
{ "command": "copy",   "fromPath": "$.AFDBERICHT.RECORDS", "toPath": "$.afdMessage.policy.coverages" },
{ "command": "remove", "path": "$.afdMessage.policy.coverages[?(@.SRT!='04')]" },
{ "command": "put",    "path": "$.afdMessage.policy.coverages[*].entityType", "value": "coverage" },
{ "command": "resolve","path": "$.afdMessage.policy.coverages[*]", "…": "…" }
```

`entityType` is written **before** the codes so that once the AFD 1.0 fields are removed the
remaining keys are in entity order — which is what makes the output key-order identical to the
car sample's input.

> The label codes and record layout are shaped after AFD 1.0's flat, numbered style rather than
> transcribed from the dictionary. Map them onto your own AFD dictionary before using this against
> a real trading partner; only the paths change when you do.
