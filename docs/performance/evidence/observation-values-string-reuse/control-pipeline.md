```

BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Unknown processor
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=250ms
WarmupCount=3

```
| Method               | Workload   | Mean           | Error         | StdDev       | Op/s        | Gen0     | Gen1     | Gen2    | Allocated  |
|--------------------- |----------- |---------------:|--------------:|-------------:|------------:|---------:|---------:|--------:|-----------:|
| **ParseDirect**          | **collection** |    **26,864.4 ns** |     **306.62 ns** |     **79.63 ns** |    **37,224.0** |   **7.2074** |   **1.6136** |       **-** |   **59.15 KB** |
| ParseThroughDocument | collection |    33,704.6 ns |      43.90 ns |      6.79 ns |    29,669.5 |   3.7634 |   0.5376 |       - |    31.5 KB |
| ParseMapWrite        | collection |    30,697.1 ns |     349.82 ns |     90.85 ns |    32,576.4 |   7.2021 |   0.6104 |       - |   59.36 KB |
| **ParseDirect**          | **flat**       |       **567.1 ns** |       **5.22 ns** |      **1.35 ns** | **1,763,294.9** |   **0.2000** |        **-** |       **-** |    **1.64 KB** |
| ParseThroughDocument | flat       |     1,425.9 ns |      63.10 ns |     16.39 ns |   701,325.2 |   0.2078 |        - |       - |    1.71 KB |
| ParseMapWrite        | flat       |       674.2 ns |       8.37 ns |      2.17 ns | 1,483,290.2 |   0.2299 |        - |       - |    1.89 KB |
| **ParseDirect**          | **large**      | **1,786,392.2 ns** |   **7,314.42 ns** |  **1,131.91 ns** |       **559.8** | **250.0000** | **138.8889** | **69.4444** | **1902.53 KB** |
| ParseThroughDocument | large      | 1,492,140.2 ns |  34,197.60 ns |  8,881.01 ns |       670.2 | 125.0000 |  62.5000 | 39.7727 |  995.89 KB |
| ParseMapWrite        | large      | 1,919,726.9 ns | 158,870.97 ns | 41,258.29 ns |       520.9 | 256.9444 | 166.6667 | 83.3333 | 1909.52 KB |
| **ParseDirect**          | **nested**     |     **1,629.1 ns** |       **9.53 ns** |      **2.47 ns** |   **613,823.3** |   **0.5609** |   **0.0130** |       **-** |    **4.61 KB** |
| ParseThroughDocument | nested     |     3,440.1 ns |      28.06 ns |      4.34 ns |   290,692.3 |   0.5760 |   0.0137 |       - |    4.71 KB |
| ParseMapWrite        | nested     |     1,900.0 ns |       7.78 ns |      2.02 ns |   526,329.2 |   0.5861 |        - |       - |    4.82 KB |
