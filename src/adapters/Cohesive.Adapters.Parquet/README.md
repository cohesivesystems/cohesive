# Cohesive.Adapters.Parquet

Parquet writing helpers for typed row and column output.

## Install

```bash
dotnet add package Cohesive.Adapters.Parquet
```

## Use When

- You need to write typed records to Parquet.
- You want column configuration and row writer helpers that fit Cohesive data export workflows.
- You are producing training, analytics, or interchange artifacts from Cohesive data.

## Example

```csharp
using Cohesive.Adapters.Parquet;
using ParquetSharp;

var writer = ParquetRowWriter.For<ShipmentExportRow>()
    .Column(new Column<string>("shipment_id"), row => row.ShipmentId)
    .Column(new Column<decimal>("total_amount"), row => row.TotalAmount)
    .ToWriter();

await using var stream = File.Create("shipments.parquet");
await writer.Write(rows, stream);
```

## Related Packages

- `Cohesive` for shared stream and domain helpers.
