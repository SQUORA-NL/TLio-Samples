```

BenchmarkDotNet v0.15.8, macOS 27.0 (26A428) [Darwin 27.0.0]
Apple M4 Pro, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a


```
| Method                                                 | Mean        | Error     | StdDev      | Gen0      | Gen1      | Gen2     | Allocated |
|------------------------------------------------------- |------------:|----------:|------------:|----------:|----------:|---------:|----------:|
| &#39;pam-simple: 1 contract, 10y quarterly (41 events)&#39;    |    558.9 μs |  10.88 μs |    16.61 μs |  208.9844 |   54.6875 |        - |   1.67 MB |
| &#39;pam-simple: 1 contract, 50y monthly (601 events)&#39;     | 10,547.4 μs | 180.30 μs |   150.56 μs | 1562.5000 |  671.8750 | 109.3750 |  27.46 MB |
| &#39;pam-reference: 1 contract, 10y quarterly (42 events)&#39; |  2,593.3 μs |  50.71 μs |    58.40 μs |  890.6250 |  390.6250 |        - |   7.55 MB |
| &#39;pam-reference: 1 contract, 50y monthly (602 events)&#39;  | 47,838.1 μs | 951.34 μs | 1,739.59 μs | 7000.0000 | 2000.0000 | 666.6667 | 122.59 MB |
