```

BenchmarkDotNet v0.15.8, macOS 27.0 (26A428) [Darwin 27.0.0]
Apple M4 Pro, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                                 | Mean        | Error       | StdDev    | Gen0      | Gen1      | Gen2      | Allocated |
|------------------------------------------------------- |------------:|------------:|----------:|----------:|----------:|----------:|----------:|
| &#39;pam-simple: 1 contract, 10y quarterly (41 events)&#39;    |    517.7 μs |    44.40 μs |   2.43 μs |  207.0313 |   54.6875 |         - |   1.67 MB |
| &#39;pam-simple: 1 contract, 50y monthly (601 events)&#39;     |  9,647.8 μs | 5,577.56 μs | 305.73 μs | 1359.3750 |  593.7500 |  125.0000 |  27.44 MB |
| &#39;pam-reference: 1 contract, 10y quarterly (42 events)&#39; |  2,495.4 μs | 1,341.23 μs |  73.52 μs |  890.6250 |  390.6250 |         - |   7.55 MB |
| &#39;pam-reference: 1 contract, 50y monthly (602 events)&#39;  | 45,479.2 μs | 8,937.21 μs | 489.88 μs | 7000.0000 | 2000.0000 | 1000.0000 | 122.59 MB |
