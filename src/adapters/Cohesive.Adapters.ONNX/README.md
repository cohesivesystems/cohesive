# Cohesive.Adapters.ONNX

ONNX Runtime adapters for Cohesive inference contracts, including embeddings, pair scoring, and feature-vector scoring.

## Install

```bash
dotnet add package Cohesive.Adapters.ONNX
```

## Use When

- You want Cohesive inference interfaces backed by ONNX Runtime sessions.
- You need embedding, cross-encoder pair scoring, or feature-vector scoring models behind provider-neutral contracts.
- You want tokenizer and score parsing helpers for ONNX model integration.

## Example

```csharp
using Cohesive.AI.Inference;
using Cohesive.Adapters.ONNX;

using var model = OnnxBiEncoderEmbeddingModel.CreateFromSentenceTransformerExport(
    exportDirectory: "models/shipping-encoder",
    modelName: "shipping-encoder",
    maxTokenCount: 256);

ReadOnlyMemory<byte>[] inputs = ["delayed shipment"u8.ToArray()];
var result = await model.EmbedAsync(new EmbeddingBatchRequest(inputs, BatchSize: 16));
```

## Related Packages

- `Cohesive.AI` for inference and text contracts.
