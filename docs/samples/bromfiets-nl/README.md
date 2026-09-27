# Bromfietsverzekering — a GIM rekenkern in TLio

> One of eight-plus samples — see the [samples index](../README.md) for the rest.

A moped/scooter ("bromfiets") insurance quote, rated **and accepted** exactly the way the source
workbook (`Nh1816 bromfiets`) does it: a driver's age and claim-free years become a bonus-malus
step, a vehicle type and catalog value are looked up in a rate table, WA (liability), casco and
accident cover are each priced, discounted, given commission and taxed, the result is summarised
the way the workbook's own "kassabon" (receipt) section does — and the whole request is run past
the same 22 acceptance rules the workbook's own validation sheet still runs live.

The source is not a script or a message format — it is an Excel **GIM rekenkern**: a `Berekening`
(calculation) sheet full of formulas, a `Tarieven` (rates) sheet full of tables, an `out_16` sheet
holding the acceptance rules, and thirty **`in_`/`out_` sheets**, one per GIM entity, each three
rows tall (index, field names, field paths) that exist only to declare the shape of the input and
output complex types — `IN_GimData_Contractdocument_Pp_Bf` is "the vehicle entity", not a sheet
with data on it. This sample is the same calculation and the same acceptance run, done as a TLio
script against ordinary JSON.

| | |
|---|---|
| Scenario | 22-year-old (age on 1 January), 5 claim-free years, Yamaha Aerox Brommer with catalog value € 1.850 (incl. accessories), approved lock, WA + casco + the one accident-cover combination this tariff prices, 5% package discount, 3% advice discount, 15% commission, paid annually, starting on the tariff date |
| Outcome | **Geaccepteerd** — **€ 337,08 totaal product premie, € 67,23 assurantiebelasting, € 404,31 totaal premie** per year |

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/bromfiets-nl/gim/input.json \
  --script docs/samples/bromfiets-nl/gim/script.json
```

The CLI prints compact JSON; the committed `output.json` is the same document, indented. The
script is deterministic — every date is a field in the request (`tariffDate`) rather than
`=datetime()`/`TODAY()`, so a run next year reproduces this output.

The CLI registers the Math, Text and TimeDate function packs and the ETL command pack
(`resolve`); a host that only calls `ParseOptions.CreateDefault()` will not resolve.

## `input.json` uses readable names for a GIM shape

The thirty `in_`/`out_` sheets declare fifteen input entities and seventeen output entities, but
only **six** of the input entities have a field the `Berekening` sheet's formulas actually read —
the rest (`Al`, `Pk_Cl`, `Pp_Cl`, `Pp_Li`, `Pp_Vp`, `Pp_Vp_Sp`, `Pp_So`, `IN_RequestData`) are
envelope, relation and administrative data the calculation never touches. `input.json` keeps only
the entities and fields that matter, with human-readable names, structured to mirror the GIM
entity layering:

| `input.json` | GIM entity.field | Meaning |
|---|---|---|
| `request.policy.packageDiscountPct` | `Pk.PkPrcpkkt` | Pakketkorting |
| `request.product.tariffDate` | *(substitutes `TODAY()`)* | See "Two ages, one reference date" below |
| `request.product.paymentTermMonths` | `Pp.PpBetterm` | Months per payment term (1/3/6/12) |
| `request.product.extraBmSteps` | `Pp.PpBmcomtr` | Extra bonus-malus steps |
| `request.product.groupDiscountPct` | `Pp.PpKrtprc1` | Groepskorting |
| `request.product.adviceDiscountPct` | `Pp.PpKrtprc2` | Advieskorting |
| `request.product.commissionPct` | `Pp.PpGwprovp` | Actual agreed commission, 0–38% |
| `request.policyholder.postcode` | `Pp_Bs.BsPcode` | Captured, unrated — this tariff has no region factor |
| `request.policyholder.birthDate` | `Pp_Bs.BsGebdat` | |
| `request.policyholder.claimFreeYears` | `Pp_Bs.BsSchdvr` | Zuivere schadevrije jaren |
| `request.vehicle.typeCode` | `Pp_Bf.BfObjsrtb` | SIVI object-sort code: 62/63/60/13 |
| `request.vehicle.brand` | `Pp_Bf.BfMerk` | Checked against the excluded-brands list |
| `request.vehicle.model` | `Pp_Bf.BfModel` | Checked against the excluded-models list |
| `request.vehicle.type` | `Pp_Bf.BfType` | Free-text trim/type — checked against the excluded-types list, distinct from `typeCode` |
| `request.vehicle.catalogValue` | `Pp_Bf.BfVwaca` | |
| `request.vehicle.accessoriesValue` | `Pp_Bf.BfVwaac` | |
| `request.vehicle.approvedLock` | `Pp_Bf.BfTnoslot` | `"J"`/`"N"` — an ART-approved lock, required when casco is selected |
| `request.product.startDate` | `Pp.PpIngdat` | Ingangsdatum — checked against the tariff date and, if present, the claim date |
| `request.claim.date` *(optional, omitted here)* | `Pp_So.SoSchadat` | Only present on a claim-driven re-quote; the "ingangsdatum before schadedatum" check is a no-op without it |
| `request.coverage.wa.code` | `Pp_Wa.WaCode` | `"02001"` selects WA |
| `request.coverage.casco.code` | `Pp_Ca.CaCode` | `"02002"` selects casco |
| `request.coverage.accidents.{code,sumDeath,sumDisability}` | `Pp_Po.{PoCode,PoVwavpa,PoVwavpb}` | This tariff prices exactly one combination: `01001` / 2.500 / 12.500 |

`output.json`'s `premiums.{wa,casco,accidents}.{bruto,netto,assurantiebelasting}` are named for
what they are, but they are the three fields the GIM output layer actually carries per coverage —
`OUT_..._Pp_Wa` etc. name them `WaBtp` / `WaNtp` / `WaTass`. `summary` carries the policy-level
totals the `Pp` output entity carries: `PpNjp`, `PpNtpexcp`, `PpNtpinka`, `PpTpp`, `PpTass`,
`PpTtot`, `PpTkrt` — see the command titles in `script.json`, which name the GIM field a value
corresponds to wherever one exists.

## Two ages, one reference date

The source worksheet computes age twice, against two different "now"s: the risk category is
looked up on the driver's age **on 1 January of the current year**, while the "too young" check
compares the birth date to `TODAY()` directly. Neither can be `=datetime()` here — a sample has to
be reproducible — so both read off one field instead, `request.product.tariffDate`, which is what
`IN_RequestData.TariffDate` exists for in the source's own request entity:

```json
{ "command": "put", "path": "$.calc.ratingAge",
  "value": "=dateDiff($.request.policyholder.birthDate,=concat(=datePart($.request.product.tariffDate,'year'),'-01-01'),'years')" }
{ "command": "put", "path": "$.calc.eligibilityAge",
  "value": "=dateDiff($.request.policyholder.birthDate,$.request.product.tariffDate,'years')" }
```

## The rate book (`tarieven`)

One `add`, ten tables — six from the `Tarieven` sheet, four from the exclusion/postcode lists on
`Basis` that the acceptance rules read — all carried over row for row:

| Table | Rows | Keyed on | Gives |
|---|---|---|---|
| `vehicleTypes` | 4 | SIVI object-sort code | vehicle type text |
| `riskCategoryBmBasis` | 5 | risk category (A–E) | starting bonus-malus step |
| `bmLadder` | 25 | bonus-malus step | premium factor |
| `waPremiums` | 20 | category × vehicle type | WA premium |
| `cascoPremiums` | 620 | vehicle type × category × catalog-value bracket (31 brackets) | casco premium |
| `accidentsTariff` | 1 | code + insured sums | accident-cover premium |
| `excludedBrands` / `excludedModels` / `excludedTypes` | 54 each | — (membership) | used by `=in(...)` in the acceptance checks |
| `dubiousPostcodes` | 136 | — (membership) | the Soft "Bel 807" check |

The source's 115-row **per-age** table (16 through 130, each mapped to a category) is not carried
over as data — every age in a band maps to the same category, so the band is a `decisionTable`
rule instead, the same move the car-insurance sample makes for its driver-age band. Five rules
replace 115 rows.

## The joins: `resolve`, twice

`riskCategory`, `vehicleType` and the casco bracket are decided first (two `decisionTable`s and
one `put`), then **one `resolve` with four settings** fetches BM basis, WA premium, casco premium
and the accident tariff in a single pass — three of those four are multi-key joins, the same
mechanism `TLio.Extensions.ETL.Commands.Resolve` uses for a single key:

```json
{ "referencesCollectionPath": "$.tarieven.cascoPremiums[*]",
  "resolveKeys": [
    { "keyPath": "@.vehicleType",     "referenceKeyPath": "@.vehicleType" },
    { "keyPath": "@.category",        "referenceKeyPath": "@.category" },
    { "keyPath": "@.maxCatalogValue", "referenceKeyPath": "@.maxCatalogValue" } ],
  "values": [ { "targetPath": "@.cascoPremium", "value": "@.premium" } ] }
```

The bonus-malus **step** is computed from the BM **basis** this same resolve just fetched
(`=clamp(=sum(bmBasis, claimFreeYears, extraBmSteps), 1, 25)`), so the ladder itself needs a
**second** `resolve` call — the step it joins on does not exist until the first one has run.

## The rating model

Every coverage goes through the same four-stage build-up, run three times (WA, casco, accidents):

```
netto basis premie   = tariefpremie × bonus-malus factor × 0,8      (0,8 strips the tariff's
                                                                       built-in 20% commission)
netto product premie = netto basis premie × (1 + pakketkorting%) × (1 + groepskorting%)
                                            × (1 + advieskorting%)
totaal product premie = netto product premie × (1 + provisie%)       ("bruto" in the AFD summary)
assurantiebelasting   = totaal product premie × 21%                  (accidents: exempt)
```

Casco has no bonus-malus factor when the source's own formula does not apply one — it does, WA
and casco both carry it. **Accidents has neither a bonus-malus factor nor a "too young" guard**,
matching the source exactly (`E16` never checks `F8`), and it is **zero unless WA is also
selected** — this tariff never sells accident cover standalone, whatever the accident-cover fields
say.

Worked out for the committed scenario: category **C**, bonus-malus step **11** → factor **0,35**.
WA: `309,88 × 0,35 × 0,8 = 86,77` netto basis → `× 0,95 × 1,00 × 0,97 = 79,96` netto product →
`× 1,15 = 91,95` totaal → `× 21% = 19,31` tax. Casco and accidents follow the same shape; the full
numbers are in `output.json`.

**Discounts are negative percentages** — `-5` means a 5% discount — matching the source
worksheet's own explicit convention ("korting is een negatief percentage"). `adviceDiscountAmount`
in the summary is therefore negative too.

## Acceptance (`OUT_GimData_Contractdocument_Xg_Xm`)

The source's `out_16` sheet is 24 rows, each an independent live formula deciding whether one
message fires — not a first-match table like the car-insurance sample's acceptance
`decisionTable`, but a **checklist**: every rule runs, and any number of them can be true at once.
That shape is what `$.acceptance` reproduces, in three commands:

1. **One object-valued `put`**, `$.calc.checks` — 22 keys, one expression each, none referencing
   the others. Nothing here needs a `decisionTable`; every rule is a single `=and`/`=or`/`=in`/
   comparison against `$.request`, `$.calc` or `$.tarieven` — the same three roots the premium
   calculation already reads.
2. **One `put`**, `$.acceptance.decision` — `=if(=or(`  …every Hard check…  `),'afgewezen','geaccepteerd')`.
   The one Soft check (a suspicious postcode) never blocks acceptance by itself.
3. **One `put`**, `$.acceptance.messages` — an array literal with one object per rule, each
   `active` field a `=fetch($.calc.checks.<name>)` of the value the first command just computed.
   This is deliberately **every rule, not just the ones that fired** — the same shape as the
   source's own output array, which lists all 24 rows with an `Active` column rather than only the
   triggered ones. `output.json`'s committed scenario passes every check, so all 22 `active` flags
   read `false` there; grep the script for `driverTooYoung` or run the CLI against a bad input to
   see them flip.

`$.tarieven` gained four more tables for this: `excludedBrands`, `excludedModels`, `excludedTypes`
(54 entries — checked with `=in(value, $.tarieven.excludedBrands)`, one call, no loop) and
`dubiousPostcodes` (136 entries, the Soft "Bel 807" check).

**Two rules stay switched off, because the source switches them off.** Rows 8 and 9 of `out_16`
are hardcoded `FALSE`, each with the same author's note next to it: the validation number and the
message text disagree (row 8 checks age 85 while its own label says "ouder dan 75"; row 9 checks
"< 16" while its label says "kleiner dan waarde 15") — *"wat is hier de bedoeling? Bespreken met
Paul W. voorlopig uitschakelen"* ("what's intended here? Discuss with Paul W., disabled for now").
That discussion never happened in the copy this sample is built from, so re-enabling either rule
here would mean inventing the business decision rather than mimicking it. They are left out, not
silently re-derived.

**Three other rules had a formula that could not mean what its own message says**, and those *are*
corrected, because the alternative was reproducing a bug rather than a rule:

| Rule | Source formula checks | Message / real tariff says | Fixed to |
|---|---|---|---|
| `waRequired` (msg 4) | `WaCode` equals unpadded `2001` | Every other WA check in the workbook uses `02001` (5 characters — the length rule two rows later requires it) | `notEquals(code, '02001')` |
| `accidentCombinationInvalid` (msg 5) | sums of 5.000/10.000 or 12.500/25.000 | This tariff's own `accidentsTariff` prices exactly one combination: 2.500/12.500 | Flags any accepted accident cover whose sums are not 2.500/12.500 |
| `catalogValueExceedsBrommobiel` (msg 20) | catalog value `> 8000` | The message says "hoger dan € 16.000", and the casco table's own top bracket for Brommobiel is 16.000 | `> 16000` |

## What this sample still leaves out, and why

- **The pakket-/groepskorting amount breakdown** (`PK_TPAKKRT`, `PP_TPKRTVM`) is a real field pair
  in the source's output layer, but its own formula sums an empty range (`E18:E30`/`F18:F30`
  instead of `E4:E16`/`F4:F16`) and evaluates to zero regardless of input — a bug in the source
  workbook, not a business rule. Rather than guess at the intended proration, this sample omits
  those two fields. `PP_TKRT` (advieskorting bedrag) uses the *correct* range in the source and is
  kept.
- **"Interne korting"** (the `D` column in `Berekening`, rows 14–16) is a literal `0` in the
  workbook with no GIM field feeding it — an underwriter override slot, not a real input — so it
  is left out rather than modelled as an always-zero field.
- **"Advieskorting toepassen?"** (`G14`/`G15`/`G16`) is hardcoded `"j"` for every coverage in the
  source, i.e. never actually a per-quote choice in this tariff version, so the script applies the
  advice discount unconditionally too, the same way.
- **Catalog values above the top casco bracket (€ 16.000)** are capped at it in the *pricing*
  `decisionTable`, rather than left to miss — the acceptance layer above is what actually declines
  a value that high (msg 18/19/20), so pricing never has to fail on it.
- **Postcode still is not rated on.** It now feeds one acceptance check (the Soft "Bel 807"
  suspicious-postcode list), but this tariff has no region factor — `postcode` never enters the
  premium build-up.

> The rate book is transcribed from the `Nh1816 bromfiets` tariff sheet as given, not independently
> verified against any insurer's current pricing — treat the numbers as illustrative, the same way
> the [samples index](../README.md) treats every rate book in this repository. GIM is a different
> thing from the SIVI AFD 2.0-style messages the other insurance samples use: AFD is an exchange
> format between parties, GIM here is the internal shape of one insurer's own rekenkern (rating
> engine) — `input.json`/`output.json` are not meant to travel over the wire as written.
