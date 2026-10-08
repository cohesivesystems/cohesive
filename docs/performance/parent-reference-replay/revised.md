```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=64  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method  | Shape      | Mean        | Error      | StdDev    | Gen0     | Gen1     | Gen2     | Allocated |
|-------- |----------- |------------:|-----------:|----------:|---------:|---------:|---------:|----------:|
| **Prepare** | **collection** | **1,473.13 μs** | **162.507 μs** | **72.154 μs** |  **31.2500** |        **-** |        **-** | **310.16 KB** |
| **Prepare** | **flat**       |   **177.92 μs** |   **8.417 μs** |  **4.402 μs** |        **-** |        **-** |        **-** |   **46.2 KB** |
| **Prepare** | **large**      | **2,028.17 μs** | **161.819 μs** | **71.849 μs** | **609.3750** | **609.3750** | **140.6250** |   **1257 KB** |
| **Prepare** | **nested**     |    **86.51 μs** |   **3.150 μs** |  **1.647 μs** |        **-** |        **-** |        **-** |  **75.26 KB** |
