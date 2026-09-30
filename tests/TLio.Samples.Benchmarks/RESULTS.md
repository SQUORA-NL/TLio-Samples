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
