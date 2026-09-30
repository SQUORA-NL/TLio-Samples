# Oracle: golden data from the real ACTUS-I engines

Two files, produced by running ACTUS-I itself, so a Tlio script can be held to the same numbers:

| File | Size | Content |
|---|---|---|
| `pam-oracle.json` | 5.6 MB | 442 PAM contracts (42 reference cases + 400 generated) run through `ActusInsurance.Core.CPU`: 22,277 events |
| `life-oracle.json` | 7.0 MB | The life-insurance engine: tables, factors, 3 projection configs, 7 scenario sets, 1,736 transition checks, 3,000 product-rule checks |

Both are deterministic: the generator was run twice and the files are byte-identical (SHA-256
`b4196b66...` for PAM, `597bb0a6...` for life). Invariant culture, doubles written in round-trip form.
Top level of both: `{ "generator", "acceleratorOrPackageVersion", ..., "cases": [...] }`.

## Versions

- **PAM:** `ActusInsurance.Core.CPU` 1.0.0-preview.2 (NuGet), `PrincipalAtMaturity.Schedule` then `Apply`,
  exactly as `ActusInsurance.Tests.CPU` does. .NET runtime 10.0.10.
- **Life:** `ActusInsurance.LifeInsurance.GPU` built from source (it is not on NuGet), ILGPU 1.5.3,
  **accelerator `CPUAccelerator` (type CPU)**, .NET 10.0.10. No CUDA or OpenCL device was used.
- **Source files the run depended on** (SHA-256), in `Actus-Insurance.GPU` under `~/dev/actus`:
  `TestData/actus-tests-pam.json` `13bef7da...`; `LifeProjectionKernel.cs` `07c6190c...`;
  `LifeGpuParameterBuilder.cs` `38a45b1b...`; `RulePack/markov_graph.json` `eb863ea6...`;
  `RulePack/rule_set.json` `14b86083...`; `RulePack/factor_set.json` `bd558727...`;
  `ProductRuleSetTests.cs` `a3d82ae1...` (only the two rule-pack JSON strings are read from it).

## `pam-oracle.json`

Header extras: `referenceCasesMatchingPublishedResults`, `generatorSeed` (20260930), `coverage`
(every term `FromDictionary` reads, whether the generator varied it, and how).

Each case: `{ id, source, terms, dataObserved, events | error }`.

- `terms`: the string map `PamContractTerms.FromDictionary` takes (values are strings, as in the reference file).
- `dataObserved`: `{ marketObjectCode: [ { timestamp, value } ] }`. ACTUS-I uses the closest previous observation.
- `events`: `{ eventDate, scheduleDate, eventType, payoff, notionalPrincipal, nominalInterestRate, accruedInterest, feeAccrued, currency }`.
  `eventDate` is the business-day-shifted date, `scheduleDate` the unshifted one. Order is ACTUS-I's own
  (sorted by date, then event type, purchase-date filter applied).
- `error`: ACTUS-I threw, or produced no events. Kept on purpose: 2 cases (`gen0014`, `gen0060`), both
  `AttributeConversionException: Failed to parse period from cycle`, caused by a deliberately unsupported cycle `P2WL0`.
- Reference cases (`pam01`..`pam42`) also carry `referenceEventCount`, `matchesReference` and
  `firstReferenceDifference`. **ACTUS-I's own CPU output matches the published reference in 42 of 42**
  (10 decimals, tolerance 2e-10, `nominalInterestRate` not compared, computed count >= reference count,
  the same rule as its own tests).

**Coverage of the 400 generated contracts.** Event types: IED, IP, MD, IPCI, RR, RRF, FP, SC, PRD, TD all occur.
Varied: role (RPA/RPL, ~3% BUY/SEL/RFL/PFL/RF/PF), all 7 day counts (AA, A360, A365, 30E360ISDA, 30E360, B252,
A336), EOM/SD, all 9 business-day conventions with calendars NC/MF/MFH, interest cycles (ISO D/M/Y incl. compound,
bare `1M`/`1Y`, L0/L1 stubs, absent) and their anchors, premium/discount at IED, rate multiplier, rate reset (cycle,
anchor, spread, market data, life and period caps/floors, `nextResetRate` for RRF), fees (basis A/N, rate, cycle,
accrued), purchase and termination with prices, capitalisation end date (IPCI), initial accrued interest, scaling
(index, effect `I00`/`0N0`/`IN0`, multipliers), status date before and after the IED.
**Supported but not varied:** currency and FX (constant USD, no FX data), `scalingIndexAtContractDealDate`
(constant 100). **Read but never used by the CPU engine:** `fixingPeriod`, `cyclePointOfInterestPayment`,
`cyclePointOfRateReset`; `contractPerformance` goes into the state only. `MFH` is built with an empty holiday set,
so it behaves like `MF`.

**Things a port has to know:**
- Cycle grammar (`CycleUtils.ParsePeriod`): ISO `P{n}Y{n}M{n}D` with optional `L0`/`L1`, or a bare `{n}{D|M|Q|H|Y}`.
  `P1Q`, `P1H`, `P2W` are rejected. Quarterly is `P3M`.
- The reference file's `nominalInterestRate` is inconsistent in `pam29`-`pam35` (0.0 in the results, 0.1 in use);
  ACTUS-I skips that column.
- `statusDate` after the IED removes the earlier events; `terminationDate` adds an IP and a TD on that date and
  removes everything later; `purchaseDate` removes earlier events and the IP on the purchase date.

## `life-oracle.json`

`cases` is a list of sections, each `{ kind, description, ... }`. `contractOrder` (header) lists the 64 contracts in the
row order of every projection section: 42 from the ACTUS-I benchmark generator (`bench01`..`bench42`) then 22 edge
contracts (ages at and beyond the table edges, gender X, +-5000 and 10000 bps loading, years-in-force 0 to 35,
every non-Active state).

| `kind` | Sections | Content |
|---|---|---|
| `tables` | 1 | Mortality (61 ages x M/F/X), lapse (31 durations x Monthly/Quarterly/Annual), disability tables **as the kernel sees them (float32, widened without change)**, the same tables rebuilt in double (`...DoublePrecision`), constants, the 10x10 Markov hazard matrix, `markov_graph.json`, `factor_set.json`, `rule_set.json` |
| `factors` | 1 | `LifeFactorEvaluator.Evaluate`: 8 factors for 64 contracts, with `evalDateDays` and `insuredDobDays` |
| `projection` | 3 | `ProjectBatch` for (12 steps, 1/12), (30 steps, 1.0), (600 steps, 1/12). Per contract: `expectedCashflow`, `probActive`, `probDeathClaimed`, `probLapsed`, one value per step |
| `scenarios` | 7 | `ProjectScenarioBatch` on the first 10 contracts for base, base/stress/favourable (annual and monthly), a custom set (lapse multipliers 0/70/130/500, shifts beyond the +-5000 clamp), `GenerateDeterministic` seeds 1 and 2 and the default seed. Per (contract, scenario): all four channels, `totalCashflow`, `survivalProbability`. Per set: `portfolioCashflowAt`, `portfolioTotalCashflow`, `cashflowStatsPerContract`, `portfolioCashflowStats` |
| `transitions` | 1 | `ValidateTransition`: each of the 12 Markov transitions x 8 contract variants x 16 fact combinations, plus every ordered state pair (self, terminal-from, undefined) with facts all false and all true. Item: from, to, `definedInGraph`, contract, facts, `allowed`, `ruleId`, `reason` |
| `productRules` | 2 | The LifestyleProtect (5 rules) and SimpleTerm (5 rules) rule packs (JSON included) evaluated over 300 seeded (meta, contract) combinations each; item: `ruleId`, contract, meta, `allowed`, `reason`, `inputsUsed` |
| `precision`, `determinism` | 1 each | See below |

**Behaviour of ACTUS-I's life engine that a port must reproduce (all visible in the data):**
1. Only two states have hazards: **Active** (mortality, lapse, disability) and **GracePeriod** (lapse). Every other
   non-terminal state (Prospect, Lapsed, PaidUp, ClaimOpen, Reinstatement) is projected with no decrement: survival stays 1
   and the premium inflow continues every step (`edge20_lapsed`, `edge22_prospect`: cashflow 1200, `probActive` 1).
2. Contracts in ClaimPaid, DeathClaimPaid or Terminated return cashflow 0, `probActive` 0, `probDeathClaimed` **1** at every step.
3. The kernel is a "table-driven" independent-decrement approximation. `disability` reduces survival but pays nothing.
   `qx` is capped at 0.999, disability at 0.05, total decrement at 1. Gender X uses the female column.
4. `ProjectScenarioBatch` is not scenario-aware: it adds the shift to `extraPremBps` (clamped +-5000) and turns the lapse
   multiplier into a `yearsInForce` shift of `-(pct - 100) * 0.05` (floored at 0), then projects as usual.
5. `ValidateTransition` denies self-transitions, transitions out of terminal states and undefined pairs before any guard runs
   (`rule_no_self_transition`, `rule_no_transition_from_terminal`, `rule_invalid_transition`), and passes `claimApproved := claimFiled`.
6. The 8-factor `LifeFactorEvaluator` is a separate CPU path (analytic, no interpolation); the projection kernel does not call it.

## Precision: a double-precision port cannot match the life kernel to 1e-10

The kernel does its age, years-in-force and table lookups in **float32** (`AgeAtEval`, `YearsInForce`, the tables, and the
interpolation fraction) and only the running probabilities in double. PAM is plain double throughout; the life numbers are not.

To ground the tolerance, three reimplementations of the kernel formulas were compared against the kernel's own output over all
64 contracts and all three configurations (`RefKernels.cs`):

| Reimplementation | Worst probability difference (abs / rel) | Worst cashflow difference (abs / rel) |
|---|---|---|
| **Faithful** (same float32/double mix) | **0 / 0: bit-identical** | 0 / 0 |
| **All double, with the exported float32 tables** (what a Tlio script fed these tables computes) | 1.2e-8 / 4.2e-7 (monthly), 1.5e-9 / 5.7e-9 (annual) | 3.1e-3 / 2.3e-5 (monthly), 2.0e-6 / 2.5e-9 (annual) |
| **All double, tables rebuilt in double** | 1.5e-8 / 5.8e-7 (monthly), 1.2e-8 / 1.1e-7 (annual) | 3.1e-3 / 6.7e-5 (monthly), 5.4e-4 / 3.6e-5 (annual) |

The largest **absolute** cashflow difference (3.1e-3) is on contracts with big death outflows (`edge13`, `edge08`: about
-1.5e4 per step, so it is about 2e-7 relative). The largest **relative** one (2.3e-5 to 6.7e-5) is `edge01`, step 10, where the
cashflow is -0.03 because premium inflow and death outflow cross zero, so a relative bound is meaningless there. The cause is
float32 rounding of `age + (float)(t * dt)` and of the tables, not a formula difference (the faithful copy is bit-identical).

**Suggested tolerances for a Tlio port** (about 3x headroom over what was measured, more for the annual case with the exported tables):
- probabilities: |difference| <= 5e-8 (measured maximum 1.5e-8, either table variant);
- expected cashflow, monthly steps: |difference| <= 1e-2 (measured 3.1e-3, either table variant);
- expected cashflow, annual steps: <= 1e-4 with the exported float32 tables (measured 2.0e-6), <= 2e-3 if the tables are rebuilt in
  double (measured 5.4e-4);
- PAM: hold it to 1e-10 as before.

**Determinism:** two consecutive `ProjectBatch` runs on the same executor were bit-identical for all three configurations, and two
full regenerations of both files were byte-identical.

## Regenerating

The generator is in `tools/actus-oracle/` (not part of the solution). It needs a scratch copy of the life library, because
that project is not published:

```sh
mkdir -p tools/actus-oracle/lib
rsync -a --exclude bin --exclude obj ~/dev/actus/Actus-Insurance.GPU/src/ActusInsurance.LifeInsurance.GPU/ \
  tools/actus-oracle/lib/ActusInsurance.LifeInsurance.GPU/
cd tools/actus-oracle
dotnet build -c Release
dotnet bin/Release/net10.0/ActusOracle.dll all out 400     # what | output dir | number of generated PAM contracts
```

`what` is `pam`, `life` or `all`. The PAM reference JSON and the product rule packs are read from
`~/dev/actus/Actus-Insurance.GPU` (read-only). The `lib/` copy is unmodified (the default executor picked the CPU accelerator
here; on a machine with CUDA or OpenCL it would pick that device, and the float32 results could differ from these).
