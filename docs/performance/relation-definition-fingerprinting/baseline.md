```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=16  
IterationCount=3  LaunchCount=1  UnrollFactor=1  
WarmupCount=1  

```
| Method      | Shape      | Mean     | Error     | StdDev    | Gen0     | Gen1     | Allocated |
|------------ |----------- |---------:|----------:|----------:|---------:|---------:|----------:|
| **Fingerprint** | **collection** | **3.245 ms** | **0.7076 ms** | **0.0388 ms** | **187.5000** |  **62.5000** |   **1.72 MB** |
| **Fingerprint** | **flat**       | **2.734 ms** | **4.8562 ms** | **0.2662 ms** | **125.0000** |  **62.5000** |    **1.2 MB** |
| **Fingerprint** | **large**      | **6.699 ms** | **3.3411 ms** | **0.1831 ms** | **375.0000** | **125.0000** |   **3.18 MB** |
| **Fingerprint** | **nested**     | **3.463 ms** | **2.2430 ms** | **0.1229 ms** | **125.0000** |        **-** |   **1.23 MB** |
