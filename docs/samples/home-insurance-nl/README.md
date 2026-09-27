# Woonhuis + inboedel — SIVI AFD 2.0 in XML

A quote request for a **woonpakket** — buildings and contents on one policy — turned into a
complete policy setup: postcode zone, roof and wall factors, building age band, contents band,
acceptance rules, premium build-up including assurantiebelasting, and the clause the thatched
roof triggers.

**The message is XML, the script is XML, and every path is XPath.** That is the point of this
sample: it is the same job the two car samples do, written in a path language that can express a
join.

| | |
|---|---|
| Scenario | 1932 farmhouse in Giethoorn (PC4 8355), **rieten kap**, 168 m², herbouwwaarde € 520.000, inboedel € 95.000, SCM-2, eigen risico € 250, halfjaarbetaling |
| Outcome | Accepted **under the rietdekkersclausule** — inspection due 2027-05-01, extra excess € 500 |
| Premium | **€ 974,64 per half-year, € 1.949,28 per year** |

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/home-insurance-nl/sivi-afd-xml/input.xml \
  --script docs/samples/home-insurance-nl/sivi-afd-xml/script.xml
```

## What XPath does that the JSON samples need a command for

Every rate-book lookup is a **predicate that references another absolute path**. The join is in
the path; there is no `resolve` in this script at all:

```xml
<put path="/afdMessage/calc/opstal"
     value="=round(=divide(=multiply(
       /afdMessage/policy/objects/object[1]/rebuildValueAmount,
       /afdMessage/rates/productRates/item[code = /afdMessage/policy/productCode]/opstalPromillage,
       /afdMessage/rates/postcodeZones/item[pc4 = /afdMessage/calc/pc4]/regionFactor,
       /afdMessage/rates/roofTypes/item[code = /afdMessage/policy/objects/object[1]/roofTypeCode]/roofFactor,
       …), 12000), 2)"/>
```

Eight tables, one expression, no lookup step in between. The car sample's equivalent is a
`resolve` with eight settings feeding a `$.calc.lookup` node that this script never needs.

The options total is the same trick applied to a set. XPath compares two node-sets as "any pair
matches", so this keeps every tariff row whose code was requested — and because opstal and
inboedel are not *in* the tariff table, the join picks out exactly the additional coverages:

```xml
<put path="/afdMessage/calc/options"
     value="=round(=sum(/afdMessage/rates/coverageTariffs/item[
              code = /afdMessage/policy/coverages/coverage/coverageCode]/premiumPerMonth), 2)"/>
```

## Two XML details worth knowing

- **`path="/"` is the document node, not the document element.** The car samples' `decisionTable`
  runs on `path: "$"`; the XML equivalent is `path="/afdMessage"`. `/` selects nothing and the
  command warns.
- **The four coverage entities are written by index**, `coverage[1]` … `coverage[4]`, so the
  order of the requested coverages is part of the message contract here, exactly as it is in the
  car sample. A predicate would do just as well — `put` on
  `coverage[coverageCode='OPSTAL']` addresses the line by its code and leaves its siblings
  alone — and is the better choice when the order is *not* contractual. The index is kept here to
  match the car sample it is meant to be read against.

## The rating model

Everything is a **monthly** amount until the payment-term step.

```
opstal    = herbouwwaarde × opstalPromillage / 12000
            × regionFactor × roofFactor × wallFactor × buildingAgeFactor
            × securityFactor × occupancyFactor × excessFactor
inboedel  = verzekerd bedrag × inboedelPromillage / 12000
            × regionFactor × securityFactor × excessFactor
options   = Σ tariff of the requested additional coverages
net/month = opstal + inboedel + options
net/term  = net/month × monthsPerTerm × termFactor
taxable   = net/term + policy costs
tax       = taxable × 21%   (assurantiebelasting)
gross     = taxable + tax
```

`/ 12000` is `/ 1000` for the per-mille and `/ 12` for the month, done once.

Worked out: `520000 × 1.35 × 0.94 × 1.85 × 1.00 × 1.22 × 0.95 × 1.00 × 0.97 / 12000 = 114.37`
opstal, `95000 × 2.10 × 0.94 × 0.95 × 0.97 / 12000 = 14.40` inboedel, `4.15 + 3.60 = 7.75`
options → `136.52` net per month → `802.74` per term → `805.49` taxable → `169.15` tax →
**€ 974,64 per half-year**.

The building-age factor is deliberately **absent** from the contents premium: contents do not age
with the building. So are the roof and wall factors, for the same reason.

## The rate book

One `add`, nine tables: `postcodeZones`, `roofTypes`, `wallTypes`, `securityClasses`,
`occupancyTypes`, `productRates`, `coverageTariffs`, `paymentTerms`, `excessDiscounts`, plus a
`rules` node for the tax rate and the two clause constants.

Three `decisionTable`s carry what is genuinely rule text rather than data: the building-age band,
the contents band that decides the guarantee against underinsurance, and acceptance. Acceptance
uses `defaultResults` as the accept path, so the rules only have to state the exceptions —
leegstand refuses, a monument or a herbouwwaarde over € 1.000.000 refers to a human, and a rieten
kap accepts with a clause.

## The clauses

Two `ifElse` blocks, because what is conditional is a **whole node** rather than a value:

- **RIET-01** — fires on `roofTypeCode = RIET`. Adds an extra excess, an inspection requirement
  and a due date six months after commencement (`=dateAdd(commencementDate, 6, 'months')`).
- **ONDERVERZEKERING-01** — fires when the contents band withheld the guarantee. It does not fire
  in this scenario; the € 95.000 sum insured lands in the `standaard` band.

`clauses` does not exist in the message until one of them writes into it — a path that does not
exist yet is a container.

> The rate book, the acceptance rules and the clause texts are illustrative and internally
> consistent, but they are not any insurer's actual tariff. See the
> [samples index](../README.md#about-the-sivi-samples) for what "AFD-shaped" means here.
