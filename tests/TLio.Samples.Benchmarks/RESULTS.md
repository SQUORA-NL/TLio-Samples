# ACTUS PAM: Tlio script against ACTUS-I CPU, same machine

Measured 30 September 2026 in one serial session (nothing else running), Release build. Every
figure below comes from that session, except where a range is given.

- **Machine:** Apple M4 Pro, 14 cores, 24 GB, macOS, .NET 10.0.302 (runtime 10.0.10)
- **Tlio:** `TLio.*` 1.1.0-preview.5 (floating `1.*-*`)
- **ACTUS-I CPU:** `ActusInsurance.Core.CPU` 1.0.0-preview.2, `PrincipalAtMaturity.Schedule` + `Apply`
- **Parallel:** `Parallel.For`, 14 threads, Server GC
- **Single contract, Tlio:** BenchmarkDotNet 0.15.8, default job, one thread, Workstation GC
- **Single contract, ACTUS-I:** Stopwatch loop, 2,000 warmed runs (mean and median). A different
  tool from Tlio's, so single-contract ratios are approximate.
- **Not measured here:** the GPU and the Ryzen 7 3800X. Nothing below is comparable to the GPU column.

## The workloads

The work per contract is the same on both sides.

| Shape | Contract | Tlio `pam-simple` | Tlio `pam-reference` | ACTUS-I |
|---|---|---|---|---|
| Short | 10 years, quarterly, A360, notional 10,000+, alternating RPA/RPL | 41 events | 42 | 42 |
| Long | 50 years, monthly, A365, 100,000 at 5 % (ACTUS-I's "Ultimate" workload) | 601 | 602 | 602 |

`pam-simple` folds the maturity IP into the MD, so it has one event fewer. It is IED, IP and MD
only: **it passes 0 of the 42 ACTUS reference cases strictly.** `pam-reference` reproduces all
42 to ten decimals, including rate reset, and is the like-for-like counterpart of ACTUS-I.

## Results: one contract, warmed

| | Tlio `pam-simple` | Tlio `pam-reference` | ACTUS-I CPU |
|---|---|---|---|
| Short (41/42 events) | 559 µs | 2.59 ms | 18.4 µs (median 17.3) |
| Long (601/602 events) | 10.5 ms | 47.8 ms | 174 µs (median 130) |

Tlio is slower than ACTUS-I CPU by about **30x** (`pam-simple`, short), **61x** (`pam-simple`,
long), **141x** (`pam-reference`, short) and **275x** (`pam-reference`, long).

## Results: portfolio, 100,000 contracts on 14 cores

| | Tlio `pam-simple` | Tlio `pam-reference` | ACTUS-I CPU |
|---|---|---|---|
| Short | 8.75 s (88 µs each) | 52.4 s (524 µs each) | 322 ms (3.2 µs each) |
| Long | 123.4 s (1.23 ms each) | 599.4 s (5.99 ms each) | 2.58 s (25.8 µs each) |

Tlio is slower by about **27x** (`pam-simple`, short), **48x** (`pam-simple`, long), **163x**
(`pam-reference`, short) and **232x** (`pam-reference`, long).

**Run-to-run spread:** the short `pam-simple` figure was 9.66 s, 8.21 s and 8.75 s in three
separate sessions on this machine. Quote it as 8 to 10 s. The other Tlio rows were run once at
this size; on the 10,000-contract runs they moved by under 10 % between sessions.

## Results: one thread

| | Tlio `pam-simple` | ACTUS-I CPU |
|---|---|---|
| 100,000 x short | 53.6 s (536 µs each) | 814 ms (8.1 µs each) |
| 10,000 x long | 80.9 s (8.09 ms each) | 910 ms (91 µs each) |

About **66x** and **89x**. Speed-up from 14 cores: Tlio 6.1x (short) and 6.6x (long); ACTUS-I 2.5x
(short) and 3.5x (long).

## Cost per event, in parallel

| | µs per event |
|---|---|
| Tlio `pam-simple` | 2.1 (short), 2.1 (long) |
| Tlio `pam-reference` | 12.5 (short), 10.0 (long) |
| ACTUS-I CPU | 0.077 (short), 0.043 (long) |

Tlio's cost per event is flat as the schedule grows, so both scripts scale linearly with the
number of events.

## Reference tests (42 ACTUS PAM cases, ten decimals)

| Script | Passing |
|---|---|
| `pam-simple.json` | 0 of 42 (2 match on cash flows alone) |
| `pam-reference.json`, including the 4 rate-reset cases | 42 of 42 |

Caveats: `nominalInterestRate` is not compared on `pam29` to `pam35` (the reference file has 0.0
there; ACTUS-I skips that column too). Not implemented, because no case needs it: caps and
floors, SC, RRF, calendars other than Monday to Friday, accrual from an anchor before the IED.

## The rows Tlio is expected to win

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
supported; a time for it is not.

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

## Reproduce

```sh
# Tlio single contract (BenchmarkDotNet, default job)
dotnet run -c Release --project tests/TLio.Samples.Benchmarks -- --filter "*"

# Tlio portfolios (the 100,000 x long pam-reference case takes about ten minutes)
DOTNET_gcServer=1 dotnet test tests/TLio.Samples.Tests -c Release --filter "FullyQualifiedName~ActusPam_BenchmarkTests&Name~Portfolio_Throughput&Name!~1000000"
DOTNET_gcServer=1 dotnet test tests/TLio.Samples.Tests -c Release --filter "FullyQualifiedName~ActusPam_BenchmarkTests&Name~Portfolio50y"
DOTNET_gcServer=1 dotnet test tests/TLio.Samples.Tests -c Release --filter "FullyQualifiedName~ActusPam_BenchmarkTests&Name~PortfolioReference"

# Reference tests
dotnet test tests/TLio.Samples.Tests --filter "FullyQualifiedName~ActusPamReference"
```

The ACTUS-I CPU harness is not in this repository (it consumes a NuGet package from another
project); it is a console project that runs `Schedule` + `Apply` over the two shapes.
