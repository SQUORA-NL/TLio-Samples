# ACTUS PAM and life insurance: Tlio scripts against ACTUS-I, same machine

Measured 30 September and 1 October 2026, serial sessions (nothing else running), Release build.

- **Machine:** Apple M4 Pro, 14 cores, 24 GB, macOS, .NET 10.0.302 (runtime 10.0.10)
- **Tlio:** `TLio.*` 1.1.0-preview.5 (floating `1.*-*`)
- **ACTUS-I:** PAM through `ActusInsurance.Core.CPU` 1.0.0-preview.2 (`Schedule` + `Apply`); life
  through a verbatim copy of the CPU baseline in ACTUS-I's `UltimateBenchmarks.cs`
- **Parallel:** `Parallel.For`, 14 threads, Server GC
- **Single contract, Tlio:** BenchmarkDotNet 0.15.8, default job, one thread, Workstation GC
- **Single contract, ACTUS-I:** Stopwatch loop (mean and median of warmed runs). A different tool
  from Tlio's, so single-contract ratios are approximate.
- **Not measured here:** the GPU and the Ryzen 7 3800X. Nothing below is comparable to the GPU column.

## Summary

1. **The Tlio scripts now match ACTUS-I's output.** PAM: 442 of 442 golden cases to ten decimals.
   Life: every area (factors, projection, scenarios, aggregation, transition guards, product
   rules) matches ACTUS-I within the tolerances measured in section "Parity".
2. **Tlio is far slower than ACTUS-I's C# on the same work.** PAM, like for like: about 170x to
   380x slower. Life projection: thousands of times slower (with a caveat on the baseline below).
   The cost is the interpreter: about 17 µs per PAM event and about 60 µs per life time step.
3. **So the case for Tlio is not speed.** It is the rows measured further down: a rule changes
   without a build, a variant is a small script change, and the script is a readable record.

## Parity with ACTUS-I

Golden data was generated from the real ACTUS-I engines
(`tests/TLio.Samples.Tests/Resources/oracle/`, regenerable with `tools/actus-oracle/`; two full
regenerations were byte-identical) and every Tlio script is tested against it in CI.

| Area | Script | Result | Difference |
|---|---|---|---|
| PAM: 42 ACTUS reference cases | `pam-reference.json` | 42 of 42 | within 2e-10 |
| PAM: 398 generated contracts (roles, 7 day counts, 9 business-day conventions, cycles, rate reset with caps/floors, fees, scaling, purchase, termination, IPCI, accrual before IED) | same | 398 of 398 | within 2e-10 |
| PAM: contracts ACTUS-I rejects | same | 2 of 2 fail too (`P2WL0`) | n/a |
| Life: 8 risk factors, 64 contracts | `life-factors.json` | 64 of 64 | bit-identical |
| Life: projection, 64 contracts x 3 configs (12 monthly, 30 annual, 600 monthly steps) | `life-project.json` | 192 of 192 | probability 1.5e-8, cashflow 3.1e-3 monthly / 5.4e-4 annual |
| Life: scenarios and aggregation, 7 scenario sets | `life-scenario.json`, `life-aggregate.json` | 7 of 7 | cashflow up to 1.1e-3 |
| Life: transition guards, 1,736 checks | `life-transition.json` | 1,736 of 1,736 | exact, incl. reason text |
| Life: product rule sets, 3,000 checks | `life-product-rules.json` | 3,000 of 3,000 verdicts; 2,858 exact reason text | see below |

The full test project passes: 556 tests. Deliberately breaking one day-count divisor failed exactly
the 52 cases that use it, so the PAM test can tell.

**Why life is not 1e-10.** ACTUS-I's kernel does its age and table arithmetic in float32. A
double-precision port cannot match that to ten decimals; the oracle measured the gap for a
faithful double port and the tolerances above are exactly those measurements (a float32 copy of the
kernel was bit-identical to it, so the formulas are understood). On a machine with CUDA the
ACTUS-I figures could differ again: the oracle ran on ILGPU's CPU accelerator.

**Known gaps, all documented in the tests:**
- 142 of 3,000 product-rule reason texts differ in the trailing digits of one number (ACTUS-I prints
  a float32-widened 0.99 as `0.99000000953674316`, TLio prints `0.9900000095367432`; same double).
  `toFixed` in TLio stops at 15 decimals.
- The scenario generator (`GenerateDeterministic`, xorshift64) is not ported: TLio has no 64-bit
  unsigned shifts or xors. Scenario lists come from the oracle as input data.
- PAM: an interest cycle of length zero (ACTUS-I loops forever, the script rejects it), a scaling
  index of 0 (fails on the division) and same-time same-type events that ACTUS-I orders with an
  unstable sort. ACTUS-I never uses currency conversion, `fixingPeriod` or the two cycle-point
  terms, so neither does the script.
- Reproduced on purpose because ACTUS-I does them (they look like quirks): an RRF event skips
  interest accrual but moves the status date; `P1M15D` counts only its largest unit; B252 is
  actual/252 on an all-business-days calendar; the 30E360ISDA maturity exception is never armed;
  events past maturity are dropped.

## The workloads

| Shape | Work | Tlio `pam-simple` | Tlio `pam-reference` | ACTUS-I |
|---|---|---|---|---|
| PAM short | 10 years, quarterly, A360, notional 10,000+, alternating RPA/RPL | 41 events | 42 | 42 |
| PAM long | 50 years, monthly, A365, 100,000 at 5 % (ACTUS-I's "Ultimate" workload) | 601 | 602 | 602 |
| Life short | 42 policies of ACTUS-I's benchmark cycle, 30 annual steps (the horizon of its published 100,000-policy row) | n/a | `life-project` | 30 steps |
| Life long | same policies, 600 monthly steps | n/a | `life-project` | 600 steps |

`pam-simple` is IED, IP and MD only; **it passes 0 of the 42 reference cases strictly** and folds
the maturity IP into the MD. `pam-reference` is the like-for-like counterpart of ACTUS-I.

## Results: PAM, one contract, warmed

| | Tlio `pam-simple` | Tlio `pam-reference` | ACTUS-I CPU |
|---|---|---|---|
| Short | 550 µs | 3.03 ms | 17.8 µs (median 16.7) |
| Long | 10.7 ms | 55.9 ms | 168 µs (median 127) |

Tlio is slower by about **31x** / **64x** (`pam-simple`, short / long) and **170x** / **332x**
(`pam-reference`).

## Results: PAM, 100,000 contracts on 14 cores

| | Tlio `pam-simple` | Tlio `pam-reference` | ACTUS-I CPU |
|---|---|---|---|
| Short | 8.75 s (88 µs each) | 70.2 s (702 µs each) | 344 ms (3.4 µs each) |
| Long | 123.4 s (1.23 ms each) | 992.5 s, 16.5 min (9.93 ms each) | 2.59 s (25.9 µs each) |

Tlio is slower by about **25x** / **48x** (`pam-simple`) and **204x** / **383x** (`pam-reference`).

The `pam-simple` portfolio rows come from the earlier session (that script did not change). Its
short row was 9.66 s, 8.21 s and 8.75 s in three sessions, and ACTUS-I's short row 322 ms and
344 ms in two, so quote ranges, not decimals: 8 to 10 s and 320 to 350 ms.

## Results: PAM, one thread

| | Tlio `pam-simple` | ACTUS-I CPU |
|---|---|---|
| 100,000 x short | 53.6 s (536 µs each) | 770 ms (7.7 µs each) |
| 10,000 x long | 80.9 s (8.09 ms each) | 857 ms (86 µs each) |

About **70x** and **94x**. Speed-up from 14 cores: Tlio 6.1x to 6.6x; ACTUS-I 2.2x to 3.0x.

## Results: life projection

| | Tlio `life-project` | ACTUS-I CPU |
|---|---|---|
| One policy, 30 annual steps, warmed | 1.68 ms | 0.09 µs |
| One policy, 600 monthly steps, warmed | 38.0 ms | 2.0 µs |
| 100,000 policies x 30 steps, 14 cores | 36.0 s (360 µs each) | 15.3 ms (0.15 µs each) |
| 10,000 policies x 600 steps, 14 cores | 86.9 s (8.69 ms each) | 100,000 policies: 29.8 ms (0.30 µs each) |
| One thread, 30 steps | 10,000 policies: 18.3 s (1.83 ms each) | 100,000 policies: 15.1 ms (0.15 µs each) |
| One thread, 600 steps | 1,000 policies: 36.1 s (36.1 ms each) | 10,000 policies: 20.1 ms (2.0 µs each) |

Per time step: Tlio about 56 to 63 µs on one thread (12 µs per step on 14 cores); ACTUS-I about
3 ns. **Read the ratios (thousands of times) with care. The baseline does not measure the same
thing:**

- ACTUS-I's CPU baseline computes the probabilities and the cashflow of every step and then
  **stores nothing** (its own comment says the values are only "consumed"). Tlio builds and returns
  the full output document: four values per step, 105 MB of allocation for one 600-step policy.
- It is a hand-written tight loop over three float arrays. The GPU kernel it stands in for is not
  faster because of a different algorithm; the algorithm is the same and Tlio interprets it.
- The published figure for the same row (734.5 ms for 100,000 policies, 7.3 µs each) is about 50
  times higher than this baseline measured here (15 ms) on the same 42-policy cycle, and the
  published PAM CPU figure (13,863.8 ms for 100,000 contracts) is about 40 times higher than what
  this baseline measures on the M4 Pro (344 ms). Ryzen against M4 does not explain that. **The
  published CPU figures come from a different code path or workload than the ones reproduced
  here, and that is unresolved.** Do not put the Tlio life or PAM numbers next to the published
  CPU or GPU numbers until it is.

## Cost per event, in parallel

| | µs per event or step |
|---|---|
| Tlio `pam-simple` | 2.1 |
| Tlio `pam-reference` | 16.7 (short), 16.5 (long) |
| Tlio `life-project` | 12 (per step, 14 cores) |
| ACTUS-I CPU, PAM | 0.082 (short), 0.043 (long) |
| ACTUS-I CPU, life | 0.005 per step |

Tlio's cost per event is flat as the schedule grows, so all its scripts scale linearly with the
number of events or steps. Reaching full ACTUS-I parity made `pam-reference` about 17% to 66%
slower than its first version (2.6 ms to 3.0 ms and 47.8 ms to 55.9 ms for one contract; 52.4 s to
70.2 s and 599 s to 992 s for 100,000): the script is 507 lines now, not 297.

## The rows Tlio is expected to win

The three measurements below were made on the first version of `pam-reference` (294 lines) and
`pam-simple`; the script is larger now, but an edit is still an edit to data.

### A rule changes

The same rule in both: interest accrues at the nominal rate plus 0.5 %. Same machine, 3 runs each.

| | Loop | Time |
|---|---|---|
| ACTUS-I (C#) | edit `ContractEvent.cs` -> `dotnet test` (build + 42 reference tests) | 1.58, 1.63, 1.59 s |
| ACTUS-I (C#) | edit -> `dotnet build` only | 1.06, 0.92, 0.92 s |
| Tlio | edit `pam-simple.json` on disk -> restart the sample host -> first HTTP 200 | 1.92 (cold), 0.28, 0.22 s |
| Tlio | edit `pam-reference.json` -> `dotnet test --no-build` (rate-bounds tests) | 0.75, 0.77, 0.77 s |

The Tlio edit took effect: the reply's total payoff moved from 15,216.67 to 17,752.78. The same
change makes all 42 of ACTUS-I's reference tests fail, as it should.

**What this does and does not show.** On a laptop, a rule change costs seconds either way; the
Tlio loop is about 5x shorter, and needs no build at all. That is a small number next to what a
rule change costs in a real organisation: the build, the pipeline, the review and the release.
None of that was measured, because none of it exists in this repository. The measured claim is
"edit to running, no build"; do not claim more than that on a slide. The C# solution here is
small (3 projects, 42 tests); a larger one takes longer to build. The sample host compiles its
scripts once at startup, so the Tlio number includes a process restart.

### A new product variant

Added an interest-rate floor and cap (ACTUS `RRLF`, `RRLC`) to `pam-reference.json`: optional
`rateFloor` and `rateCap` terms applied to the initial rate and every reset rate. About 6 changed
lines in a 294-line script, 8 tests with hand-computed expected payoffs (floor binds, cap binds,
both, neither, floor above the initial rate, cap below it, and absent equals unreachable), and all
42 reference cases still pass. The edit-to-verified loop is 0.75 to 1.0 s. The time to *write* the
variant is not reported: it was written by Claude, not a human, so it says nothing about a
developer or an actuary. The claim "a new variant is a small script change with a test" is
supported; a time for it is not. The same holds at the scale of the whole engine: the parity work
that added rate reset variants, fees, scaling, purchase and termination to the script was done by
Claude in a few hours, again not a human figure.

### "Why did this contract pay this?"

What TLio gives natively: `context.TraceCollector` receives one `TraceEntry` per command that ran
(command name, target path, outcome, matched count, detail). It does **not** carry the source
expression, the position in the script, or a version. So the engine has per-command tracing, not
field-level lineage.

What was built here (`PamLineage` in `ActusPamLineageTests.cs`, about 210 lines): a static walk of
the command list, including `ifElse`, `forEach` and `while` bodies, that reports for a field every
command that can write it (verb, position, source expression, guards), what removes it, and which
inputs it depends on. It says "nothing in this version writes that path" when that is true, and
it does not invent array indices. Answers are cross-checked dynamically: changing a claimed input
changes the output, changing an unclaimed one does not.

Limits: it says what *can* write a field, not what did on one run (the runtime trace does that);
it over-approximates dependencies (the payment cycle is listed as an input of the total payoff
although the total does not move on A360); it cannot follow computed paths such as `=indirect()`;
the scripts carry no version or approver, and the "what changed in the next version" diff is not
built. Read-out for one field:

```
field       $.events[*].nominalInterestRate
written by  #50.commands[26]  set, inside an object literal written to $.events[*]
source      =fetch($.w.st.rate)
runs        only under: forEach $.events
and         1 earlier writer(s): #36
removed by  #51.ifScript[1]  (if =fetch($.w.hasPurchase) > where $.events[?(@.drop==true)])
```

## What building it ran into (TLio findings)

- **No fail or assert command.** Rejecting an unsupported cycle string needed a trick:
  `regexExtract` asked for a capture group its pattern does not have.
- **Number-to-text depends on the machine's culture.** `concat` and `toString` render 0.5 as `0,5`
  on a Dutch machine; `format('{0}', x)` and `toFixed` are invariant. The same trap hit the
  test harnesses (`JValue.ToString()`), and was the cause of 150 of the first 160 PAM failures.
  Booleans render as `True` (wrap in `toLower`).
- **Quoted literals that start with `$` are read as paths**, and `==` inside a quoted literal
  collapses to `=`, which breaks JSONPath filters built with `concat`. Workarounds: the `$$` escape,
  indexing by position, `decisionTable`.
- **`indirect` needs a path held in a field**, not an inline expression, and `last(indirect(...))`
  fails. A bare `$.path` string inside an object literal is not resolved; use `=fetch($.path)`.
- **`clamp` requires ordered bounds**, so ACTUS's `min(max(...))` with reversed bounds needs
  `min` and `max`.
- **No `exp()`** (`pow(2.718281828459045, x)` is accurate here), **no bitwise or 64-bit unsigned
  operators**, and `toFixed` stops at 15 decimals.
- **No sub-routines**: the date-shift and accrual blocks are written out inline, which is why the
  PAM script is 507 lines.
- **No way to share a read-only node between executions**, so a lookup table is rebuilt or cloned
  for every contract. The life script computes the two neighbouring grid points analytically
  instead; measured on one contract, tables as a script literal or as input were 12% to 15% slower
  than the analytic form and up to 1.9e-6 different from it (float32 rounding).
- **`decisionTable` evaluates result expressions lazily**, only for the matching rule: a good fit
  for guards and product rules.
- **`add` skips silently when the field already exists**, so a caller can bypass the command that
  was meant to set it; the lineage tool reports such fields as "defaults".
- `dayCountFraction` has no actual/actual-ISDA (`AA`); it is scripted.

## Reproduce

```sh
# Tlio single contract, PAM and life (BenchmarkDotNet, default job)
dotnet run -c Release --project tests/TLio.Samples.Benchmarks -- --filter "*"

# Tlio portfolios (the 100,000 x long pam-reference case takes about sixteen minutes)
DOTNET_gcServer=1 dotnet test tests/TLio.Samples.Tests -c Release --filter "FullyQualifiedName~ActusPam_BenchmarkTests&Name~Portfolio_Throughput&Name!~1000000"
DOTNET_gcServer=1 dotnet test tests/TLio.Samples.Tests -c Release --filter "FullyQualifiedName~ActusPam_BenchmarkTests&Name~Portfolio50y"
DOTNET_gcServer=1 dotnet test tests/TLio.Samples.Tests -c Release --filter "FullyQualifiedName~ActusPam_BenchmarkTests&Name~PortfolioReference"
DOTNET_gcServer=1 dotnet test tests/TLio.Samples.Tests -c Release --filter "FullyQualifiedName~ActusPam_BenchmarkTests&Name~PortfolioLife"

# Parity against ACTUS-I's golden data
dotnet test tests/TLio.Samples.Tests --filter "FullyQualifiedName~ActusPamOracle|FullyQualifiedName~ActusLife|FullyQualifiedName~ActusPamReference"
```

The ACTUS-I CPU harnesses (PAM `Schedule` + `Apply`, and the life baseline copied from
`UltimateBenchmarks.cs`) are not in this repository: they consume packages and sources from another
project. Both are small console projects; the oracle generator in `tools/actus-oracle/` documents
how to set up the ACTUS-I life library, which is not on NuGet.
