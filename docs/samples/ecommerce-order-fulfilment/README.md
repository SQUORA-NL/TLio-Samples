# Webshop order → warehouse pick list

A B2B order comes in from the webshop; the warehouse needs a pick list. Every line gets its bin
location and VAT rate, the shipment gets a carrier from its weight, the lines get sorted into
walking order, and a hazardous line adds a handling note.

Fourteen commands. Deliberately not a rating engine — this is what a **simple** transform looks
like next to the insurance samples.

| | |
|---|---|
| Scenario | 4 lines, 241 kg, bakery supplies to IJmuiden, one line of cleaning agent |
| Outcome | Pallet carrier, ADR handling note, lines resequenced A → B → C → D |
| Total | **€ 563,38 incl. VAT** (goods € 403,60 + shipping € 62,00 + VAT € 97,78) |

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/ecommerce-order-fulfilment/input.json \
  --script docs/samples/ecommerce-order-fulfilment/script.json
```

## `resolve` on every line at once

Two tables joined to every order line in one command — the per-element form of `resolve`, which
is the JSON answer to the XPath predicate the XML samples use:

```json
{ "command": "resolve", "path": "$.order.lines[*]",
  "resolveSettings": [
    { "referencesCollectionPath": "$.ref.pickLocations[*]",
      "resolveKeys": [ { "keyPath": "@.sku", "referenceKeyPath": "@.sku" } ],
      "values": [
        { "targetPath": "@.pickLocation", "value": "@.location" },
        { "targetPath": "@.pickZone",     "value": "@.pickZone" } ] },
    { "referencesCollectionPath": "$.ref.vatRates[*]",
      "resolveKeys": [ { "keyPath": "@.vatCategory", "referenceKeyPath": "@.category" } ],
      "values": [ { "targetPath": "@.vatRatePercent", "value": "@.ratePercent" } ] } ] }
```

## Why the line totals come from the order

`lineAmountExclVat` arrives already calculated, and that is not laziness. **A function argument
resolves against the document root**, so `@.quantity` inside a function does not mean "this
line" — per-element arithmetic is not available. Everything the script computes, it computes once
on scalar fields:

```json
{ "command": "put", "path": "$.calc.goodsExclVat",
  "value": "=round(=sum($.order.lines[*].lineAmountExclVat),2)" }
```

This is the single constraint that shapes every array-heavy TLio script. Design around it by
having per-line values arrive as data or come from a lookup.

## Decide, then price

The carrier **band** is a `decisionTable` — weight ranges are rule text. What the band **costs**
is a `resolve` against a table. Keeping those apart is the point:

```
kg <= 23         → PAKKET   → PostNL,   € 7,95
23 < kg <= 100   → COLLI    → DHL,      € 18,50
kg > 100         → PALLET   → Nedcargo, € 62,00
```

241 kg → PALLET.

## `sortby` and `distinct`

The picker should walk the aisles in order, not in the order the customer clicked. One function
call reorders the array as it is written into the pick list:

```json
"lines": "=sortby($.order.lines,'pickLocation')",
"zones": "=distinct($.order.lines[*].pickZone)"
```

The customer ordered A, C, B, D; the pick list reads A-01-14, B-03-11, C-07-02, D-02-05.

## The handling note

An `ifElse`, because what is conditional is a whole node. The condition counts the lines that
landed in the hazardous pick zone — a value the `resolve` above put there, so the rule reads
against warehouse data rather than against the SKU:

```json
"condition": "=greaterThan(=count($.order.lines[?(@.pickZone=='GEVAAR')]),0)"
```

## Known simplification

VAT is charged at one blended 21% on the whole order. Each line already carries its own
`vatRatePercent` from the lookup, but summing per rate group would need per-element arithmetic —
see above. A real invoice splits VAT per rate group; doing that in TLio means having the
per-group subtotals arrive as data, or splitting the lines into one array per rate first.
