```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method       | Shape    | Mean         | Error         | StdDev       | Gen0     | Gen1     | Gen2    | Allocated |
|------------- |--------- |-------------:|--------------:|-------------:|---------:|---------:|--------:|----------:|
| **Canonicalize** | **escaped**  | **714,258.1 ns** | **179,985.04 ns** | **94,135.66 ns** | **218.7500** | **218.7500** | **62.5000** |  **394152 B** |
| **Canonicalize** | **flat**     |     **147.6 ns** |      **23.15 ns** |     **10.28 ns** |        **-** |        **-** |       **-** |     **636 B** |
| **Canonicalize** | **repeated** | **623,507.3 ns** |  **29,743.86 ns** | **13,206.46 ns** | **218.7500** | **218.7500** | **62.5000** |  **398360 B** |
| **Canonicalize** | **unique**   | **249,009.4 ns** |   **6,326.59 ns** |  **2,256.12 ns** |  **31.2500** |        **-** |       **-** |  **369496 B** |
