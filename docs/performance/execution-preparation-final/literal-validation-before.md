```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M5 Max, 1 CPU, 18 logical and 18 physical cores
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Scenario           | Mean         | Error       | StdDev      | Allocated |
|--------- |------------------- |-------------:|------------:|------------:|----------:|
| **Validate** | **enum-first**         |     **6.644 ns** |   **0.0308 ns** |   **0.0137 ns** |         **-** |
| **Validate** | **enum-invalid**       |   **511.426 ns** |   **0.6325 ns** |   **0.3308 ns** |     **209 B** |
| **Validate** | **enum-last**          |   **262.105 ns** |   **3.1206 ns** |   **1.6321 ns** |       **1 B** |
| **Validate** | **named-alias**        | **1,461.143 ns** |   **1.5239 ns** |   **0.6766 ns** |       **1 B** |
| **Validate** | **named-first**        |    **30.159 ns** |   **1.1777 ns** |   **0.6160 ns** |       **1 B** |
| **Validate** | **named-invalid**      | **3,038.933 ns** |  **81.9501 ns** |  **42.8615 ns** |     **169 B** |
| **Validate** | **named-last**         | **1,551.392 ns** |   **3.6218 ns** |   **1.6081 ns** |       **1 B** |
| **Validate** | **object-first-error** | **3,195.966 ns** |  **90.5117 ns** |  **40.1878 ns** |     **457 B** |
| **Validate** | **object-last-error**  | **8,292.310 ns** |  **43.7119 ns** |  **19.4084 ns** |     **465 B** |
| **Validate** | **object-valid**       | **3,899.767 ns** | **214.8524 ns** | **112.3719 ns** |       **1 B** |
| **Validate** | **union-first**        |    **43.706 ns** |   **1.3704 ns** |   **0.7167 ns** |       **1 B** |
| **Validate** | **union-invalid**      | **2,073.789 ns** |  **99.2192 ns** |  **51.8936 ns** |     **313 B** |
| **Validate** | **union-last**         |   **993.136 ns** |   **2.1711 ns** |   **0.9640 ns** |       **1 B** |
