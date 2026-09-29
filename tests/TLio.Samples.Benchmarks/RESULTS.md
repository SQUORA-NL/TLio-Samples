# ACTUS PAM: Tlio script against ACTUS-I CPU, same machine

Measured 29 September 2026. Every row ran alone (nothing else running), Release build.

- **Machine:** Apple M4 Pro, 14 cores, 24 GB, macOS, .NET 10.0.302 (runtime 10.0.10)
- **Tlio:** `TLio.*` 1.1.0-preview.5 (floating `1.*-*`), script `samples/TLio.Sample.Actus.Api/Scripts/pam-simple.json`
- **ACTUS-I CPU:** `ActusInsurance.Core.CPU` 1.0.0-preview.2, `PrincipalAtMaturity.Schedule` + `Apply`
- **Parallel:** `Parallel.For`, 14 threads, Server GC, one shared compiled script (Tlio) / no shared state (ACTUS-I)
- **Not measured here:** the GPU, and the Ryzen 7 3800X. Nothing below is comparable to the GPU column.

Two contract shapes, so the work per contract is the same on both sides:

| Shape | Contract | Tlio events | ACTUS-I events |
|---|---|---|---|
| 41 events | 10 years, quarterly, A360, notional 10,000+, alternating RPA/RPL | 41 | 42 |
| 602 events | 50 years, monthly, A365, 100,000 at 5 % (ACTUS-I's "Ultimate" workload) | 601 | 602 |

Event counts differ by one: ACTUS-I emits a separate IP at maturity, `pam-simple` folds it into the MD.

## Results

| Measure | Tlio `pam-simple` | ACTUS-I CPU | Tlio is slower by |
|---|---|---|---|
| **One contract, 41 events, warmed** | 537 µs (BenchmarkDotNet mean) | 17 µs mean, 16.5 µs median | about 31x |
| **One contract, 602 events, warmed** | 10.0 ms (BenchmarkDotNet mean) | 172 µs mean, 134 µs median | about 58x |
| 100,000 x 41 events, one thread | 52.6 s (526 µs each) | 769 ms (7.7 µs each) | about 68x |
| **100,000 x 41 events, 14 cores** | **8.21 s** (82 µs each) | **331 ms** (3.3 µs each) | **about 25x** |
| 10,000 x 602 events, one thread | 81.6 s (8.16 ms each) | 865 ms (86 µs each) | about 94x |
| **100,000 x 602 events, 14 cores** | **121.6 s** (1.22 ms each) | **2.53 s** (25 µs each) | **about 48x** |
| Speed-up from 14 cores | 6.4x (41 ev.), about 6.7x (602 ev.) | 2.3x (41 ev.), 3.4x (602 ev.) | |
| Cost per event, parallel | about 2.0 µs | 0.08 µs (41 ev.), 0.04 µs (602 ev.) | |

Tlio cost per event is flat (about 2 µs) from 41 to 601 events, so it scales linearly with the
schedule length. Single-contract figures use different tools on each side (BenchmarkDotNet
ShortRun for Tlio, a 2,000-run Stopwatch loop for ACTUS-I), so treat the ratio as approximate.

## Like for like: `pam-reference` against ACTUS-I CPU

`pam-simple` is IED, IP, MD only. ACTUS-I CPU runs the full PAM semantics, so the table above
**understates** the real difference. `pam-reference.json` is the script that reproduces all 42
ACTUS reference cases (rate reset, fees, day counts, business days, purchase/termination), so it
is the fair counterpart. It emits the maturity IP separately, like ACTUS-I: 42 and 602 events.

| Measure | Tlio `pam-reference` | ACTUS-I CPU | Tlio is slower by |
|---|---|---|---|
| One contract, 42 events, warmed | 2.50 ms | 17 µs | ~146x |
| One contract, 602 events, warmed | 45.5 ms | 172 µs | ~264x |
| 10,000 x 42 events, 14 cores | 4.98 s (498 µs each) | 331 ms per 100,000, so ~33 ms | ~150x per contract |
| 100 x 602 events, 14 cores | 956 ms (9.56 ms each; 1,000 contracts: 5.93 s, 5.93 ms each) | 25 µs each | ~235x per contract |

Cost per event for `pam-reference` is about 12 µs in parallel (498 µs / 42), 6x `pam-simple`.
The parallel figures are per contract at smaller portfolios than the `pam-simple` rows (a
100,000 x 602 run would take about ten minutes); the per-contract cost is flat across sizes for
both scripts, so it can be scaled, but it was not run at 100,000.

## What was checked

| Reference tests (42 ACTUS PAM cases, 10 decimals) | Passing |
|---|---|
| `pam-simple.json` as it is | 0 of 42 (2 match on cash flows alone) |
| `pam-reference.json`, including the 4 rate-reset cases | 42 of 42 |

Caveats on the 42: `nominalInterestRate` is not compared on `pam29`-`pam35` (the reference file
has 0.0 there; ACTUS-I skips that column too). Not implemented because no case needs it: caps and
floors, SC, RRF, calendars other than Monday to Friday, accrual from an anchor before the IED.

## Reproduce

```sh
# Tlio single contract (BenchmarkDotNet)
dotnet run -c Release --project tests/TLio.Samples.Benchmarks -- --filter "*" --job short

# Tlio throughput, both shapes
DOTNET_gcServer=1 dotnet test tests/TLio.Samples.Tests -c Release --filter "FullyQualifiedName~ActusPam_BenchmarkTests&Name~Portfolio50y"
DOTNET_gcServer=1 dotnet test tests/TLio.Samples.Tests -c Release --filter "FullyQualifiedName~ActusPam_BenchmarkTests&Name~Portfolio_Throughput&Name!~1000000"

# Reference tests
dotnet test tests/TLio.Samples.Tests --filter "FullyQualifiedName~ActusPamReference"
```

The ACTUS-I CPU harness is not in this repository (it consumes a NuGet package from another
project). The single-contract Tlio run was BenchmarkDotNet `--job short` (3 iterations, 18 %
margin); rerun without `--job short` before quoting it to a decimal.
