# Overlijdensrisicoverzekering — SIVI AFD 2.0 in XML

A term-life quote linked to a mortgage, turned into a policy: mortality band, smoker and
profession loadings, benefit shape, duration factor, a waiver-of-premium rider priced as a
percentage, medical acceptance, and the two clauses the case triggers.

**The message is XML, the script is XML, every path is XPath.** Where the
[woonhuis sample](../home-insurance-nl/) shows a one-column join, this one shows a **two-column**
join — and a product with no assurantiebelasting at all.

| | |
|---|---|
| Scenario | Man born 1988-06-30, non-smoker, kantoorberoep. € 350.000 annuïtair dalend over 30 years, monthly premium, policy pledged to the lender |
| Outcome | Accepted on the short health declaration; two clauses added |
| Premium | **€ 20,09 per month, € 241,08 per year** |

```sh
dotnet run --project samples/TLio.Sample.Cli -- \
  --input  docs/samples/life-insurance-nl/sivi-afd-xml/input.xml \
  --script docs/samples/life-insurance-nl/sivi-afd-xml/script.xml
```

## A two-column key is still one step

A mortality table is keyed on band **and** gender. In JSON that is a `resolve` with two entries in
`resolveKeys`; in XPath the predicate takes both:

```xml
value="=fetch(/afdMessage/rates/mortalityRates/item[
         ageBand = /afdMessage/calc/ageBand
         and gender = /afdMessage/policy/parties/party[1]/genderCode]/ratePerMille)"
```

and it drops straight into the premium expression alongside four single-column joins.

## The rating model

Life is rated **annually and then split**, where the property samples rate monthly and multiply
up. Nothing about TLio decides that — the rate book does.

```
risk premium/year = kapitaal / 1000 × mortalityRate(band, gender)
                    × smokerFactor × professionFactor × benefitFactor × durationFactor
rider             = risk premium × surchargeRate
premium/year      = risk premium + rider
net/month         = premium/year / 12
chargeable        = net/term + policy costs
tax               = chargeable × 0%   ← exempt
gross             = chargeable + tax
```

Worked out: `350 × 0.87 × 1.00 × 1.00 × 0.62 × 1.15 = 217.11` risk premium, `× 0.08 = 17.37`
rider, `= 234.48` per year, `/ 12 = 19.54` per month, `+ 0.55` policy costs → **€ 20,09 per
month**.

### The tax step is kept, and computed

A life premium is exempt from assurantiebelasting. The script still runs the step, with a rate of
`0.00` read from the rate book:

```xml
<put path="/afdMessage/calc/insuranceTax"
     value="=round(=multiply(/afdMessage/calc/chargeableBase,
                             /afdMessage/rates/rules/insuranceTaxRate), 2)"/>
```

The exemption is then visible on the policy as a rate, an amount and a reason, rather than as a
step that quietly is not there. The working field is called `chargeableBase`, not `taxableBase`,
for the same reason — nothing downstream of it is taxable.

## The rider guard

A rider is a percentage of the risk premium, not a flat tariff, and it may not be requested at
all. `=sum()` over an empty match fails, so the count is checked first — and because `=if`
evaluates **only the branch it picks**, the multiply never runs when there is no rider:

```xml
value="=round(=if(=greaterThan(=count(/afdMessage/policy/coverages/coverage[
                    coverageCode='PREMIEVRIJSTELLING-AO']), 0),
                  =multiply(/afdMessage/calc/baseAnnual,
                            /afdMessage/rates/riderRates/item[
                              code='PREMIEVRIJSTELLING-AO']/surchargeRate),
                  0), 2)"/>
```

## Dates

Two ages, both calendar arithmetic on text:

- `ageAtStart` is `=dateDiff(dateOfBirth, commencementDate, 'years')` — dated from the
  **commencement** date, not the quote date, because that is when the cover and the mortality
  band start.
- `ageAtEnd` is that plus `durationYears`, and the acceptance table refuses anything past 70.
- `expiryDate` is `=dateAdd(commencementDate, durationYears, 'years')` — the full term, not one
  year. A life policy is not annually renewable.

## Acceptance and clauses

One `decisionTable` with three inputs and `defaultResults` as the accept path: end age past 70
refuses, a capital over € 750.000 needs a medical examination, and a smoker over € 300.000 needs
the long health declaration. This case falls through to the short declaration.

Two `ifElse` blocks, both firing:

- **VERPANDING-01** — the policy is pledged to the mortgage lender, which changes who may alter
  it. Carries the pledgee's name through from the request.
- **EINDLEEFTIJD-01** — the policy runs to age 68, past 65, which has to be stated because the
  premium was rated on a band that stops at 70.

> The mortality table and loadings are illustrative and internally consistent, but they are not
> any insurer's actual tariff. See the [samples index](../README.md#about-the-sivi-samples) for
> what "AFD-shaped" means here.
