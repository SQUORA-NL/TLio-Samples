# pam-reference.json

The Tlio version of ACTUS-I's PAM engine (`ActusInsurance.Core.CPU`, `PrincipalAtMaturity.Schedule` then
`Apply`). It reproduces ACTUS-I's output on all 442 contracts in
`tests/TLio.Samples.Tests/Resources/oracle/pam-oracle.json` (the 42 published reference cases and 400 seeded
generated ones), to ten decimals, and it fails on the two contracts where ACTUS-I throws.
`ActusPamOracleTests` runs them; `ActusPamReferenceTests` runs the 42 reference cases on their own.

`pam-simple.json` is a different, smaller script (IED, IP, MD only) and is what the portfolio benchmark times.

Every command carries a `title` and a `description`; the sections below are the titled ones, in order.

| Section | What it does |
|---|---|
| Defaults | The values ACTUS-I's `FromDictionary` fills in when a term is missing. Life and period caps and floors default to +-1e300 in place of infinity. |
| Derived switches | Role sign (RPA, BUY, RFL, RF are +1; RPL, SEL, PFL, PF are -1), the day count spelling, whether a business-day convention moves anything (only with calendar MF or MFH, and never with NOS), fee accrual, scaling effect. |
| The four cycles | Interest payment, rate reset, fee and scaling, each with its anchor and whether it adds the maturity date. |
| Read the cycles | Parses ACTUS cycle strings (`P1M15DL1`, `3M`) inside the script; see below. |
| Schedules | One `TYPE\|date` token per event, with end-of-month, long-stub and capitalisation-end (IPCI) handling. |
| Single events | IED, MD, purchase (PRD), termination (IP and TD), capitalisation end. |
| Sort keys | Business-day shifting, the RRF rule, the drops (before the status date, after a termination, after maturity), and ACTUS's same-day event priority. |
| Initial state | Notional, rate, accrued interest (given, or derived from the last interest date before the status date), accrued fee, scaling multipliers. |
| Evaluate the events | Year fraction, payoff and state update per event. |

## Input

The flat document the scripts have always read (`contractRole`, `initialExchangeDate`, `interestPaymentAnchor`, `marketData`, ...),
plus the terms the oracle exercises: `nextResetRate`, `lifeFloor`, `lifeCap`, `periodFloor`, `periodCap`, `scalingEffect`,
`marketObjectCodeOfScalingIndex`, `scalingIndexAtContractDealDate`, `scalingAnchor`, `scalingData` (`[{ timestamp, value }]`),
`notionalScalingMultiplier`, `interestScalingMultiplier`. `rateFloor` and `rateCap` are a variant on top of ACTUS (see
`ActusPamRateBoundsTests`), applied after ACTUS's own bounds.

A cycle is given either as the raw ACTUS string (`cycleOfInterestPayment`, `cycleOfRateReset`, `cycleOfFee`,
`cycleOfScalingIndex`) or, as before, as an object (`interestPaymentCycle`, `rateResetCycle`, `feeCycle`:
`{ count, unit, longStub }`). The string wins when both are present.

## An unsupported cycle fails the run

ACTUS-I accepts an ISO period `P{n}Y{n}M{n}D` or a bare `{n}D`, `{n}M`, `{n}Q`, `{n}H`, `{n}Y`, optionally followed by `L0`
(long stub) or `L1` (short stub, the default). `P1Q`, `P1H`, `P2W` and any other stub character throw. This script does the same:
`ExecutionResult.Success` is false and the log says why. TLio has no fail command, so the script asks `regexExtract` for a
capture group its pattern does not have, which is an authoring error and stops the run.

## Behaviour copied from ACTUS-I because the oracle needs it

These look odd; they are what the engine does.

- **A compound cycle counts its largest unit only.** `P1M15D` is one month, `P1Y6M` is one year, `P0Y0M45D` is 45 days.
- **RRF does not accrue.** ACTUS-I leaves it out of its accrual list yet still moves the status date, so the interest between the
  previous event and an RRF is never accrued.
- **Initial accrued interest** uses A365 or A360 (never the contract's own convention) on the days between the last interest date
  before the status date and the status date.
- **30E/360 ISDA** treats the last day of any month as the 30th, February included; ISDA's maturity exception is never armed.
- **B252** is the actual day count over 252, because ACTUS-I hands it a calendar in which every day is a business day.
- **Events after maturity are dropped**, including a maturity that a business-day convention pushes past itself.
- **A scaling index with no observation at or before the event date** is 0, so the multiplier becomes 0.
- **Day count for times of day:** a time of 12:00 or later counts as the next day (ACTUS-I rounds to full hours).

## Not reproduced

- **An interest cycle of length zero** (`P0D`, `P`) is rejected. ACTUS-I loops forever on it.
- **A scaling index at the deal date of 0** fails the run on the division. ACTUS-I divides to infinity or NaN.
- **Two events with the same shifted time and the same type but different scheduled dates** are ordered by scheduled date.
  ACTUS-I sorts them with an unstable sort, so no rule reproduces its order in general; the oracle has no such pair.
- Currency conversion (ACTUS-I fixes the rate at 1), `fixingPeriod`, `cyclePointOfInterestPayment` and `cyclePointOfRateReset`
  (read and never used by the engine).
