# TLio.Sample.Actus.Api

A demo API for the ACTUS **PAM** (Principal at Maturity) contract type — bullet loans, term
deposits, zero-coupon or interest-bearing bonds — implemented entirely as TLio scripts under
`Scripts/`, built the same way `TLio.Sample.AfdApi` demonstrates SIVI AFD conversion.

The point of this sample isn't just "PAM in TLio" — it's that PAM's own algorithm (see the
[ACTUS reference implementation](https://github.com/actusfrf/actus-core)) is a loop over a
*computed* count: how many interest-payment dates fall between issue and maturity isn't known
until you walk the cycle, and each one has to be folded into a running state (principal, accrued
interest, rate) in order. TLio had no iteration primitive for that until now —
`TLio.Extensions.Looping` (`forEach`/`while`) is what makes this a genuine TLio script rather
than a native calculator wrapped in one command. Both scripts here use it for exactly that:
`while` walks the interest-payment cycle to build the schedule, `forEach` folds each date's
day-count fraction and payoff against the running state — reading and writing the current
schedule date through the ordinary `@` relative-path token
(`docs/ai-ref/commands/ForEach.md`), the same one `decisionTable`/`resolve` already use.

**Building the schedule without a special "append" command.** Neither `forEach` nor `while` has
an `appendTo` — `add` only ever appends at a literal "next free" array index, which a loop body
has no way to compute for itself, and inventing one would be exactly the kind of special-casing
this package deliberately avoids (see [ForEach.md](https://github.com/SQUORA-NL/TLio/blob/main/docs/ai-ref/commands/ForEach.md#building-a-second-array-while-iterating)).
Instead, `while` builds the schedule as a comma-separated string (`=concat(...)`) and
`split()`s it once afterward; `forEach` then transforms each date in place via `set path="@"`
into the full event object — no second array ever needs to be built during a loop.

## Run it

```bash
cd samples/TLio.Sample.Actus.Api
dotnet run
```

```bash
curl -s -X POST http://localhost:5299/actus/pam \
  -H "Content-Type: application/json" --data @SampleInput/pam-bullet-loan.json | python3 -m json.tool
```

Or import `TLio-Actus-Demo.postman_collection.json` into Postman — it exercises both endpoints
against all four bundled sample inputs (plain, envelope with no overrides, early termination,
rate change), each request asserting its expected `events`/`summary` values.

`GET /` lists both endpoints and every bundled sample file; `GET /samples/{name}` serves one
directly. Both endpoints return `{ ...input, events, summary }` — the input document with an
`events` array (one entry per cashflow, `{ date, type, payoff }`) and a `summary`
(`totalPayoff`, `eventCount`) appended — HTTP 200 on success, HTTP 422 (with a `warnings` array)
when the engine itself reports a failure.

## The two endpoints

- **`POST /actus/pam`** (`Scripts/pam-simple.json`) — bare contract terms in, cashflows out. No
  wrapping, no scenario.
- **`POST /actus/pam/envelope`** (`Scripts/pam-envelope.json`) — the same contract wrapped in
  `{ "contract": {...}, "scenario": {...} }`, where `scenario` can override behaviour without
  touching the base contract:
  - `scenario.earlyTermination: { date, settlementAmount }` — truncates the schedule at `date`
    and closes the contract with a `TD` event paying `settlementAmount` plus any interest
    accrued since the last regular payment, instead of running to maturity.
  - `scenario.rateChange: { date, newRate }` — from `date` onward, interest accrues at
    `newRate` instead of the contract's original `nominalInterestRate`.
  - Either, both, or neither may be present — `SampleInput/pam-envelope-no-overrides.json`
    (`"scenario": {}`) produces byte-identical `events`/`summary` to the plain endpoint on the
    same contract, which is the whole point of an envelope: the base computation is unchanged
    unless a scenario explicitly asks for something different.

## Contract terms shape

```json
{
  "contractId": "PAM001",
  "contractRole": "RPA",
  "currency": "EUR",
  "notionalPrincipal": 100000,
  "nominalInterestRate": 0.03,
  "dayCountConvention": "A360",
  "initialExchangeDate": "2025-01-01",
  "maturityDate": "2030-01-01",
  "interestPaymentCycle": { "count": 1, "unit": "years" }
}
```

`contractRole` is `"RPA"` (Real Position Asset — you hold the loan/bond, so `IED` is a cash
outflow and `MD` an inflow) or `"RPL"` (Real Position Liability — the reverse). This mirrors
ACTUS's `ContractRoleConvention.roleSign`. `dayCountConvention` is one of `"A360"`, `"A365"`,
`"30E360"` (`=daycountfraction(...)`, `TLio.Extensions.TimeDate`).

## Deliberate simplifications versus the ACTUS standard

This demonstrates the engine capability, not a certified ACTUS engine — several corners are cut
on purpose:

- **No ACTUS cycle-string grammar.** Real ACTUS cycles are ISO 8601 durations with a stub
  marker (`"P1YL1"`). Here `interestPaymentCycle` is a plain `{ count, unit }` TLio's own
  `dateAdd` already understands. `pam-simple.json` expects the schedule to land exactly on
  `maturityDate` (no stub-period handling); `pam-envelope.json` *does* handle a non-aligned
  `scenario.earlyTermination.date` — it appends it to the schedule as a final "stub" entry when
  the cycle wouldn't otherwise land on it.
- **No end-of-month or business-day-convention adjustment.**
- **Three day-count conventions**, not ACTUS's full set (no ISDA actual/actual, plain 30/360,
  or adjusted variants).
- **Only `IED`, `IP`, `MD`, `RR`-equivalent (via `scenario.rateChange`), and `TD`-equivalent
  (via `scenario.earlyTermination`) event behaviour is modelled** — no `FP` (fee), `SC`
  (scaling), `PRD` (purchase), or `IPCI` (capitalization).
- **Rate changes apply from the start of the next full accrual period**, not from the exact
  reset date within a partial period — real ACTUS `RR`/`STF_RR_PAM` accrues at the old rate up
  to the reset instant and only switches for what remains of that period.
- **Early termination bundles the stub interest into the `TD` payoff** as one line, rather than
  ACTUS's separate accrued-interest-at-termination `IP` event plus a clean `TD` settlement.

## Life insurance (the ACTUS-I life engine as TLio scripts)

Six more scripts under `Scripts/` reproduce ACTUS-I's life-insurance engine, and are held to the
numbers ACTUS-I itself produces (`tests/TLio.Samples.Tests/Resources/oracle/`, tests
`ActusLife*Tests`). Two are served here:

- **`POST /actus/life/project`** (`life-project.json`) — forward projection of one contract:
  `{ contract, timeSteps, dtYears }` in, `steps[]` out, each `{ expectedCashflow, probActive,
  probDeathClaimed, probLapsed }`. Try `SampleInput/life-project-example.json`.
- **`POST /actus/life/validate-transition`** (`life-transition.json`) — may this contract move
  from state A to state B? `{ contract, from, to, evalDateDays, facts }` in, `result { allowed,
  ruleId, reason }` out. The Markov graph and the guard rules are `decisionTable` rows.
  Try `SampleInput/life-transition-example.json`.

Not served, but in the same folder and tested: `life-factors.json` (the eight risk factors),
`life-scenario.json` (apply a mortality / lapse scenario to a contract), `life-aggregate.json`
(portfolio cashflow, totals and min / max / mean over a scenario cube) and
`life-product-rules.json` (per-product underwriting rules, one row per rule).

Precision: the projection matches ACTUS-I's kernel to 1.5e-8 in a probability and 3e-3 in a
monthly cashflow, not to ten decimals — the kernel does its lookups in float32 and a script computes
in double. The factors, guards and product rules are exact (one number-formatting difference is
named in `ActusLifeProductRulesTests`).

## What it does not do

No auth, no request size limits beyond ASP.NET Core's defaults, no try/catch around a
genuinely unparseable request body beyond the JSON-parse check already in `Program.cs`.
