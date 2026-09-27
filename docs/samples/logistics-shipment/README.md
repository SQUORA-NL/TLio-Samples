# Shipment booking → carrier manifest

A freight booking becomes a carrier manifest: weights and volumes totalled, chargeable weight
decided, the customs regime derived from where the goods are going, tariff data attached to every
goods line, and an export-licence hold raised on the one line that needs it.

**The data is YAML and the script is YAML** — the only sample where both are. YAML uses the same
`$.a.b` path language as JSON, so the commands are character-for-character what the JSON notation
would hold; only the syntax around them differs.

| | |
|---|---|
| Scenario | Rotterdam → Oslo, 3 packages, 1770 kg, 4,22 m³, € 51.600 of machine parts |
| Customs | NL (EU) → NO (EER) ⇒ **UITVOER-EER**, declaration `EX-A`, proof `EUR.1` |
| Hold | Mineral oil under HS 2710.19 is licence-bound ⇒ shipment held |

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/logistics-shipment/input.yaml \
  --script docs/samples/logistics-shipment/script.yaml
```

## Chargeable weight is a choice between two values

Road freight charges on whichever is greater: actual weight, or volumetric weight at 333 kg per
cubic metre. That is one field with two sources, so it is `=if` rather than an `ifElse` block —
and `=if` evaluates only the branch it picks:

```yaml
- command: put
  path: $.calc.chargeableWeightKg
  value: '=round(=if(=greaterThan(=multiply($.calc.volumeM3,333),$.calc.grossWeightKg),=multiply($.calc.volumeM3,333),$.calc.grossWeightKg),1)'
```

4,22 m³ × 333 = 1405 kg, which is less than 1770 kg, so the actual weight wins.

## Seed the keys before you resolve on them

`resolve` matches a key **on the node it is given**, so that node has to hold the key first. This
costs a command and is easy to forget — a missing key means the resolve matches nothing, silently,
and every `=fetch` that depended on it leaves its own expression text in the document:

```yaml
- command: put
  path: $.calc
  value:
    originCode: '=fetch($.booking.consignor.countryCode)'
    destinationCode: '=fetch($.booking.consignee.countryCode)'
```

Then one `resolve` with **two settings joins the same country table twice**, on different keys —
origin and destination.

> The origin of a *shipment* is the consignor's country. The country of origin of the *goods* is a
> different question, and the manifest answers both: `customs.originCountries` is
> `=distinct($.booking.goods[*].countryOfOrigin)`, which is `[NL, DE]` here, not `NL`.

## The customs regime is a rule, not a lookup

Whether a shipment needs an export declaration is a statement about two customs unions, so it is a
`decisionTable` with two inputs and `defaultResults` as the fall-through:

```
EU → EU            INTRACOMMUNAUTAIR    no declaration
EU → EER           UITVOER-EER          EX-A, EUR.1
EU → DERDE-LAND    UITVOER-DERDE-LAND   EX-A, certificate of origin
anything else      ONBEKEND             handmatig
```

The default is deliberately *not* an accept path here. An unrecognised country pair should stop
and ask a human, not quietly clear customs — which is the opposite of how the insurance samples
use `defaultResults`, and for the same reason: the default should be the safe answer.

## The licence hold

`resolve` attaches `licenceRequired` to every goods line from the tariff table; the `ifElse` then
counts the lines that came back `JA`. The rule reads against resolved tariff data rather than
against a hard-coded list of HS codes, so adding a licence-bound chapter is a data edit:

```yaml
condition: "=greaterThan(=count($.booking.goods[?(@.licenceRequired=='JA')]),0)"
```

## YAML quoting worth knowing

Every expression is quoted. An unquoted `=fetch(...)` is fine to YAML but a value starting with
`*`, `&`, `{` or `[` is not, so quoting them all is the habit that does not bite.

Inside a single-quoted YAML scalar a literal quote is doubled, which stacks with TLio's own
single-quoted arguments:

```yaml
from: '=concat($.booking.consignor.city,'', '',$.booking.consignor.countryCode)'
```

reaches TLio as `=concat($.booking.consignor.city, ', ', $.booking.consignor.countryCode)`. Using
a double-quoted YAML scalar avoids the doubling entirely, which is why the address lines are
written that way instead:

```yaml
addressLine: "=concat($.booking.consignor.street,' ',$.booking.consignor.houseNumber)"
```

Note also `'NO'` in the country table and `'0150'` in the postal code — unquoted, YAML reads the
first as boolean **false** and strips the leading zero from the second.

## Known simplification

Duty is looked up per line but never applied — the manifest states `dutyRatePercent`, it does not
compute duty payable. That would need value × rate per line, and **per-element arithmetic is not
available**: a function argument resolves against the document root, so `@.valueEur` inside a
function does not mean "this line". See the [samples index](../README.md#constraints-these-samples-are-shaped-around).
