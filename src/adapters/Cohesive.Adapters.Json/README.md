# Cohesive.Adapters.Json

JSON Schema helpers and validators for Cohesive shape graph documents.

## Install

```bash
dotnet add package Cohesive.Adapters.Json
```

## Use When

- You want JSON Schema documents for Cohesive shape graph payloads.
- You need validation of JSON documents against Cohesive shape graph schemas.
- You want schema artifacts packaged alongside the adapter for external tooling.

## Example

```csharp
using Cohesive.Adapters.Json;

JsonSchemaExporter.ExportToDirectory(
    [ShapeGraphDocumentJsonSchemaProvider.Instance],
    directory: "schemas");

var validation = ShapeGraphDocumentValidator.ValidateJson(
    File.ReadAllText("shape-graph.json"));
```

## Related Packages

- `Cohesive` for the core shape graph model.
