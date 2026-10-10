```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.201
  [Host] : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

Job=ShortRun  Toolchain=InProcessEmitToolchain  InvocationCount=4096  
IterationCount=8  LaunchCount=1  UnrollFactor=1  
WarmupCount=3  

```
| Method   | Scenario           | Mean         | Error      | StdDev     | Allocated |
|--------- |------------------- |-------------:|-----------:|-----------:|----------:|
| **Validate** | **enum-first**         |     **7.685 ns** |  **0.0394 ns** |  **0.0175 ns** |         **-** |
| **Validate** | **enum-invalid**       |   **126.184 ns** |  **3.9389 ns** |  **2.0601 ns** |     **210 B** |
| **Validate** | **enum-last**          |    **32.588 ns** |  **1.6504 ns** |  **0.8632 ns** |       **1 B** |
| **Validate** | **named-alias**        |    **51.755 ns** |  **0.6900 ns** |  **0.3064 ns** |       **1 B** |
| **Validate** | **named-first**        |    **23.995 ns** |  **0.3782 ns** |  **0.1978 ns** |       **1 B** |
| **Validate** | **named-invalid**      |   **117.898 ns** |  **3.6692 ns** |  **1.9191 ns** |     **169 B** |
| **Validate** | **named-last**         |    **52.298 ns** |  **0.8272 ns** |  **0.4327 ns** |       **1 B** |
| **Validate** | **object-first-error** | **3,307.946 ns** | **51.2333 ns** | **26.7960 ns** |     **458 B** |
| **Validate** | **object-last-error**  | **8,401.034 ns** | **71.9086 ns** | **37.6096 ns** |     **466 B** |
| **Validate** | **object-valid**       | **3,839.464 ns** | **83.8426 ns** | **43.8513 ns** |       **1 B** |
| **Validate** | **union-first**        |    **44.129 ns** |  **1.6527 ns** |  **0.7338 ns** |       **1 B** |
| **Validate** | **union-invalid**      |   **196.818 ns** |  **4.8941 ns** |  **2.1730 ns** |     **314 B** |
| **Validate** | **union-last**         |    **68.812 ns** |  **0.9734 ns** |  **0.5091 ns** |       **1 B** |
