# Car insurance rating — Dutch market (two samples)

> Two of eight samples — see the [samples index](../README.md) for the rest, including the
> [AFD 1.0 migration](../sivi-afd1-to-afd2/) whose output *is* this sample's `sivi-afd/input.json`,
> and the two XML products ([woonhuis](../home-insurance-nl/), [leven](../life-insurance-nl/))
> that do this same job without a `resolve`, because an XPath predicate can express the join.

Two end-to-end samples that turn a **quote request** into a **complete policy setup**:
postcode, vehicle-model and bonus-malus lookups, an age calculation, seven rating factors,
acceptance rules, premium build-up including assurantiebelasting, and the resulting policy
document.

The split is deliberate:

- **`input.json` is only what the insurer receives** — the quote request. No rates, no tables,
  no policy fields.
- **`script.json` carries the rate book**, added by a single `add` command at the top, and
  removed again at the end. Everything after that command only *looks things up* in it.

| | Document model | Scenario |
|---|---|---|
| [`native/`](native/) | The insurer's own request/response shape | 34-year-old in Amsterdam, 2019 Golf, allrisk accepted as requested → **€ 63.77 per month, € 765.24 per year** |
| [`sivi-afd/`](sivi-afd/) | SIVI AFD 2.0-**style** message (entities, code lists) | 23-year-old in Enschede, 2013 Corsa, allrisk **downgraded** to beperkt casco + young-driver clause → **€ 112.13 per quarter, € 448.52 per year** |

Both scripts carry the **same rate book, byte for byte**. Only the paths that address the
document differ, because the message model differs — which is the point of the pair.

## Running them

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/car-insurance-nl/native/input.json \
  --script docs/samples/car-insurance-nl/native/script.json
```

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/car-insurance-nl/sivi-afd/input.json \
  --script docs/samples/car-insurance-nl/sivi-afd/script.json
```

The CLI prints compact JSON; the committed `output.json` is the same document, indented.
Both scripts are deterministic — they date everything from `quotedOn` / `messageDate` in the
request rather than `=datetime()`, so a run tomorrow still reproduces `output.json`.

The CLI registers the Math, Text and TimeDate function packs and the ETL command pack
(`resolve`); a host that only calls `ParseOptions.CreateDefault()` will not resolve.

## The rate book in the script

One `add` command, ~235 lines of data, nine tables:

| Table | Rows | Keyed on | Gives |
|---|---|---|---|
| `postcodeZones` | 78 | PC4 | region, zone, `regionFactor` |
| `vehicleModels` | 54 | model code | make, model, fuel, weight class, kW, `waBasePremium`, `cascoRate`, `limitedCascoRate` |
| `bonusMalusLadder` | 20 | BM step | `waFactor`, `cascoFactor`, discount label |
| `coverageTariffs` | 10 | coverage code | description, premium per month |
| `paymentTerms` | 4 | M / K / H / J | months per term, terms per year, `termFactor`, policy costs |
| `excessDiscounts` | 9 | excess amount | `excessFactor` |
| `securityClasses` | 6 | SCM class | `securityFactor` |
| `parkingTypes` | 5 | parking code | `parkingFactor` |
| `usageTypes` | 5 | usage code | `usageFactor` |

Adding a row is a data edit, not a script change — that is what makes this shape worth using.
In a service you would `add` the rate book from your own store instead of inlining it; nothing
below that command changes.

## `title` and `description` — scripts that document themselves

A JSON script has no comments, so `title` and `description` are properties on **every**
command. They live on `CommandBase`, each parser binds them like any other string property, and
nothing reads them during execution: they are for whoever opens the file next.

```json
{ "command": "put", "path": "$.calc.driverAge",
  "title": "Driver age",
  "description": "Whole years on the quote date. dateDiff counts birthdays, so it is right on the birthday and right in a leap year.",
  "value": "=dateDiff($.request.applicant.birthDate,$.request.quotedOn,'years')" }
```

Every command in both samples carries both, nested ones included — the `put` inside an `ifElse`
is a command like any other. The convention they follow here: **the title says what the step is,
the description says why it is written that way.** A reader skimming titles gets the ten-step
outline below without reading a single path.

Two things to know. In **XML notation** they are attributes only — a `<title>` child element
would be part of the value being written — and `"name"` is not free for this purpose, because
`rename` uses it as the new name.

## Ages and products

**Dates are strings.** In JSON, XML and YAML alike a date is text, so an age is a function call,
not subtraction. `dateCompare` answers *which is earlier*; `dateDiff` answers *how many*:

```json
{ "command": "put", "path": "$.calc.driverAge",
  "value": "=dateDiff($.request.applicant.birthDate,$.request.quotedOn,'years')" }
```

`'years'` means birthdays passed — calendar arithmetic, so it is right on the birthday and right
in a leap year. The same call shape gives licence years and vehicle age.

**Arithmetic takes paths directly**, so nothing here goes through a string:

```json
{ "command": "put", "path": "$.calc.netPerMonth", "value": "=round(=sum($.calc.wa,$.calc.casco,$.calc.options),2)" }
{ "command": "put", "path": "$.calc.wa",
  "value": "=round(=multiply($.calc.lookup.waBasePremium,$.calc.lookup.regionFactor,$.calc.ageFactor,$.calc.mileageFactor,$.calc.lookup.usageFactor,$.calc.lookup.bmWaFactor),2)" }
```

`multiply` is variadic like `sum`; `divide` is binary like `subtract`. The casco premium is the
one that needs both — a product over the current value, at a monthly twelfth of the annual rate.

**A value produced three ways is still one command.** Casco is priced from one of two rates, or
not at all, and `=if` chooses between the three *values* rather than between three blocks. Only
the branch it picks is evaluated, so the arithmetic for a cover that was not granted never runs:

```json
{ "command": "put", "path": "$.calc.casco",
  "value": "=if(=equals($.calc.grantedCover,'casco'),=round(=divide(=multiply($.request.vehicle.currentValue,$.calc.lookup.cascoRate,$.calc.lookup.regionFactor,$.calc.lookup.bmCascoFactor,$.calc.lookup.excessFactor,$.calc.lookup.securityFactor,$.calc.lookup.parkingFactor),12),2),=if(=equals($.calc.grantedCover,'beperkt-casco'),=round(=divide(=multiply($.request.vehicle.currentValue,$.calc.lookup.limitedCascoRate,$.calc.lookup.regionFactor,$.calc.lookup.securityFactor,$.calc.lookup.parkingFactor),12),2),0))" }
```

`ifElse` is still the right command when what is conditional is a whole node — the two policy
clauses at the end of both scripts are exactly that. `=if` is for a field with two or three
sources; `ifElse` is for a field that may not be there at all.

**A ladder is arithmetic until it stops being arithmetic.** The bonus-malus step is claim-free
years plus two, floored at 1 and capped at 20 — twenty `decisionTable` rows saying one sum:

```json
{ "command": "put", "path": "$.calc.lookup.bmStep",
  "value": "=clamp(=sum($.request.cover.claimFreeYears,2),1,20)" }
```

The *factors* on each step stay a table, because those really are twenty independent numbers.
The age and mileage bands stay tables too: a band is a range, and a range is rule text.

### These samples are why those functions exist

Both scripts were originally written before `dateDiff`, `dateAdd`, `multiply` and `divide`
existed, and the workarounds were instructive enough to be worth recording. An age was digit
arithmetic on the date text —

```
=floor(=calculate(=concat('(',=replace(quotedOn,'-',''),'-',=replace(birthDate,'-',''),')/10000')))
(20260821 - 19911104) / 10000 = 34.97…  → floor → 34
```

— which is correct on the birthday by construction and wrong for any unit other than years. A
product went through `=calculate('a*b')`, whose single expression string had to be assembled by
`concat` from the fetched values, so every premium round-tripped through text and
`DataTable.Compute`. The renewal date was built by slicing the year out, adding one, and gluing
the month and day back on.

Rewriting the twenty expressions produced **byte-identical output** for both samples — which is
the point: the functions did not change the rating, they made it legible. The second pass, which
added the titles and descriptions, took `clamp` and `if` to the twenty-row ladder and the nested
`ifElse` blocks and produced byte-identical output again. `docs/function-gaps.md` is the analysis
that came out of reading these two files.

## The rating model

Everything is a **monthly** amount until the payment-term step.

```
liability (WA) = waBasePremium × regionFactor × ageFactor × mileageFactor × usageFactor × bmWaFactor
casco          = currentValue × cascoRate / 12 × regionFactor × bmCascoFactor × excessFactor × securityFactor × parkingFactor
beperkt casco  = currentValue × limitedCascoRate / 12 × regionFactor × securityFactor × parkingFactor
options        = Σ tariff of the requested optional coverages
net per month  = liability + casco + options
net per term   = net per month × monthsPerTerm × termFactor
taxable base   = net per term + policy costs
tax            = taxable base × 21%   (assurantiebelasting)
gross per term = taxable base + tax
gross per year = gross per term × termsPerYear
```

Worked out for `native/`: `21.40 × 1.28 × 1.00 × 1.00 × 1.00 × 0.40 = 10.96` liability,
`14200 × 0.052 / 12 × 1.28 × 0.50 × 0.92 × 0.97 × 1.00 = 35.14` casco, `3.25 + 2.10 = 5.35`
options → `51.45` net → `52.70` taxable → `11.07` tax → **€ 63.77 per month**.

## How the script gets there, in ten steps

1. `add` the rate book.
2. Three `put`s: driver age, licence years, vehicle age — all `dateDiff`.
3. One node — `$.calc.lookup` — collects every lookup key, including the bonus-malus step:
   `=clamp(=sum(claimFreeYears,2),1,20)`. The native sample fills the node with two `merge`s
   from the request plus the PC4 `substring` and that one `put`; the SIVI sample maps AFD field
   names onto the same key names in a single object `put`.
4. **One `resolve` with eight settings** fills that node from eight tables at once. Both scripts
   contain this block identically.
5. Four `decisionTable`s: age band, mileage band, granted cover, acceptance. Their conditions are
   ranges (`">=24 && <30"`, `"<=10"`) — rule text, which is what keeps them tables. The granted-cover
   table has four outputs, one of them the casco basis the premium step reads.
6. A second `resolve` prices the requested optional coverages, per array element.
7. Eight `put`s build the premium: one product each for WA and casco, then sums, tax and totals.
8. Object-valued `put`s write whole entities — the policy header, policyholder, vehicle, risk
   profile, coverages, premium — with their `=fetch(...)` strings evaluated inside the object.
9. Two `ifElse` blocks add the young-driver and cover-downgrade clauses. They stay blocks because
   what is conditional is a whole node's existence, not a value.
10. `remove` drops the working area and the rate book; the native sample `rename`s `request` to
    `quote`, so what remains is the request as received plus the policy it produced.

### What the new functions took out of these files

The rewrite that added titles also shortened both scripts, and every cut came from a function
rather than from dropping a feature — the output is unchanged in both samples.

| Was | Is | Because |
|---|---|---|
| 20-rule `decisionTable` for the bonus-malus step | one `put` | `=clamp(=sum(years,2),1,20)`. The twenty rows were one sum, written out. |
| `ifElse` inside `elseScript` of an `ifElse`, four `put`s deep, for the casco premium | one `put` | `=if` picks between *values*, and evaluates only the branch it picks. |
| A second `put` inside the young-driver block to overwrite `totalExcess` | folded into the object | one `=if` at the point the field is first written. |
| Ten scalar `put`s for the policy header (native) | one object-valued `put` | `=fetch(...)` inside an object value is evaluated as the object is written. |

Native went from 58 commands to 41, SIVI from 46 to 40, and the deepest nesting in either file
is now one `put` inside one `ifElse`.

## Notes and constraints worth knowing

- **`resolve` writes with `"value": "@.field"`** — a value-level relative path, resolved against
  the *matched reference entry*. A `"sourcePath"` key (as used in `docs/showcase/`) is not read
  by the converter and silently writes nothing.
- **`referencesCollectionPath` must select the elements**, i.e. `$.rates.postcodeZones[*]`, not
  `$.rates.postcodeZones`.
- **Function arguments resolve against the document root.** `@.field` inside a function call
  does not mean "this array element", so per-element arithmetic is not available. Both samples
  are shaped around that: per-line amounts come from a lookup (`resolve`), and everything
  computed is computed once on scalar fields and written to a named entity.
- **Optional coverages must be present.** `=sum()` over an empty match fails; a request without
  optional coverages needs an `ifElse` guard around that step.
- **Inside one object value, a later expression cannot read an earlier one.** The value is built
  in a copy that is not attached to the document until the command finishes, so
  `{"first": "=sum($.a,$.b)", "second": "=multiply($.out.first,10)"}` leaves `second` as text —
  `$.out.first` does not exist yet. Chain through *separate commands*, which is why `$.calc` is
  built up step by step here, or nest the calls in one expression:
  `=multiply(=sum($.a,$.b),10)`. Within one expression the inner result does feed the outer.
- **The SIVI script addresses entities by their code, not by their position.** The regular
  driver is `parties[?(@.partyRoleCode=='REGELMATIGE-BESTUURDER')]`, the vehicle
  `objects[?(@.objectTypeCode=='MOTORRIJTUIG')]`, the two rated coverages
  `coverages[?(@.coverageCode=='WA')]` and `[?(@.coverageCode=='CASCO')]`. AFD does not fix the
  order of the entities inside a collection, so `parties[1]` is an assumption the message never
  made; the filter says what is actually meant and reads that way too. It costs nothing — the
  predicate is JSONPath, evaluated by the same fetcher as any other path.

## About the SIVI AFD 2.0 sample

`sivi-afd/` is **shaped after** SIVI AFD 2.0 / AFS: an envelope with a message header, a `policy`
entity holding `parties`, `objects`, `coverages` and `premium` entities, each tagged with
`entityType`, and coded values (`partyRoleCode`, `objectTypeCode`, `coverageCode`,
`premiumFrequencyCode`). The attribute names follow that style but are an **illustrative subset,
not a certified AFD schema** — `riskProfile` and `acceptance` in particular are insurer
extensions, not AFD entities. Map the names onto your own AFD dictionary before using this
against a real trading partner; only the paths change when you do.
