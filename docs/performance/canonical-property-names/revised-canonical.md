```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method       | Shape    | Mean         | Error        | StdDev       | Gen0     | Gen1     | Gen2    | Allocated |
|------------- |--------- |-------------:|-------------:|-------------:|---------:|---------:|--------:|----------:|
| **Canonicalize** | **escaped**  | **701,901.8 ns** | **65,728.80 ns** | **34,377.44 ns** | **218.7500** | **218.7500** | **62.5000** |  **394124 B** |
| **Canonicalize** | **flat**     |     **167.1 ns** |     **38.18 ns** |     **16.95 ns** |        **-** |        **-** |       **-** |     **642 B** |
| **Canonicalize** | **repeated** | **668,587.6 ns** | **23,018.81 ns** | **12,039.29 ns** | **218.7500** | **218.7500** | **62.5000** |  **398350 B** |
| **Canonicalize** | **unique**   | **264,608.2 ns** |  **8,278.13 ns** |  **3,675.54 ns** |  **31.2500** |        **-** |       **-** |  **369497 B** |
