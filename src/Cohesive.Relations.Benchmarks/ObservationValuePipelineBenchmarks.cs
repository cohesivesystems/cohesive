using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Construction and complete JSON mapping costs, with input generation excluded.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationValuePipelineBenchmarks
{
    byte[] input = null!;
    readonly RelationQueryExpressionEvaluator evaluator = new();
    readonly RelationQueryExpressionContext context = new();
    readonly ArrayBufferWriter<byte> output = new();
    Expr mapping = null!;

    /// <summary>Flat, deeply nested, collection-heavy, and bounded-large JSON inputs.</summary>
    [Params("flat", "nested", "collection", "large")]
    public string Workload { get; set; } = "flat";

    /// <summary>Prepares bytes and expressions and checks both parser interpretations before timing.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var rows = Workload == "large" ? 4096 : Workload == "collection" ? 128 : 1;
        var depth = Workload == "nested" ? 32 : 0;
        var source = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(source))
        {
            if (Workload == "flat")
            {
                writer.WriteStartObject();
                for (var field = 0; field < 16; field++) writer.WriteNumber("field" + field, field);
                writer.WriteEndObject();
            }
            else
            {
                for (var level = 0; level < depth; level++)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("child");
                }
                writer.WriteStartObject();
                writer.WritePropertyName("items");
                writer.WriteStartArray();
                for (var row = 0; row < rows; row++)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("amount", row + 1);
                    writer.WriteString("description", "load <&> café 🚚");
                    writer.WriteBoolean("active", row % 2 == 0);
                    writer.WriteNull("optional");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                for (var level = 0; level < depth; level++) writer.WriteEndObject();
            }
        }
        input = source.WrittenSpan.ToArray();
        var path = string.Concat(Enumerable.Repeat("child.", depth)) + "items";
        mapping = Workload == "flat"
            ? Expr.Add(Expr.Field("item.field0"), Expr.Field("item.field15"))
            : Expr.Call(ExprFunctionNames.Sum, Expr.Field("item." + path), Expr.Field("item.amount"));
        var direct = ParseDirect();
        if (!direct.Equals(ParseThroughDocument())) throw new InvalidOperationException("Parser values differ.");
        if (Evaluate(direct).GetDecimal() != (Workload == "flat" ? 15m : (decimal)rows * (rows + 1) / 2))
            throw new InvalidOperationException("Unexpected mapping total.");
        _ = ParseMapWrite();
    }

    /// <summary>Builds a fresh owned value directly from UTF-8 tokens.</summary>
    /// <returns>The fresh immutable value tree.</returns>
    [Benchmark]
    public ObservationValue ParseDirect()
    {
        var reader = new Utf8JsonReader(input);
        reader.Read();
        return ObservationJsonReader.ReadValue(ref reader);
    }

    /// <summary>Builds the same value through a temporary JSON document and the existing element projector.</summary>
    /// <returns>The fresh immutable value tree after disposing the document.</returns>
    [Benchmark]
    public ObservationValue ParseThroughDocument()
    {
        using var document = JsonDocument.Parse(input);
        return ObservationValue.FromJsonElement(document.RootElement);
    }

    /// <summary>Parses, evaluates a native relation expression, and writes the result into a reusable output sink.</summary>
    /// <returns>The output byte count; output bytes remain owned by this benchmark instance.</returns>
    [Benchmark]
    public int ParseMapWrite()
    {
        var result = Evaluate(ParseDirect());
        output.Clear();
        using var writer = new Utf8JsonWriter(output);
        result.WriteTo(writer);
        writer.Flush();
        return output.WrittenCount;
    }

    ObservationValue Evaluate(ObservationValue value) => evaluator.Evaluate(mapping, context.WithCurrentItem(value));
}

/// <summary>Warm aggregate costs, isolating source retention and selector evaluation from parsing.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationAggregateBenchmarks
{
    readonly RelationQueryExpressionEvaluator evaluator = new();
    readonly RelationQueryExpressionContext context = new();
    Expr expression = null!;

    /// <summary>The canonical aggregate being evaluated.</summary>
    [Params(ExprFunctionNames.Sum, ExprFunctionNames.Avg, ExprFunctionNames.Min, ExprFunctionNames.Max)]
    public string Function { get; set; } = ExprFunctionNames.Sum;

    /// <summary>Source cardinality, including empty and bounded-large inputs.</summary>
    [Params(0, 128, 4096)]
    public int Count { get; set; }

    /// <summary>Whether each item requires a nested field read.</summary>
    [Params(false, true)]
    public bool Selector { get; set; }

    /// <summary>Creates source storage and expressions outside the measured operation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var values = new ObservationValue[Count];
        for (var i = 0; i < values.Length; i++)
        {
            var amount = ObservationValue.FromInt64(i + 1);
            values[i] = Selector
                ? ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["amount"] = amount })
                : amount;
        }
        var source = Expr.Const(ObservationValue.FromArray(values));
        expression = Selector ? Expr.Call(Function, source, Expr.Field("item.amount")) : Expr.Call(Function, source);
        var result = Evaluate();
        var expected = Function switch
        {
            ExprFunctionNames.Sum => (decimal)Count * (Count + 1) / 2,
            ExprFunctionNames.Avg => (Count + 1m) / 2,
            ExprFunctionNames.Min => 1m,
            _ => Count
        };
        if (Count == 0 && Function != ExprFunctionNames.Sum
            ? result.Kind != ObservationValueKind.Undefined
            : result.GetDecimal() != expected)
            throw new InvalidOperationException("Unexpected aggregate result.");
    }

    /// <summary>Evaluates the aggregate over an already-owned immutable source.</summary>
    /// <returns>The aggregate value, preserving empty-input semantics.</returns>
    [Benchmark]
    public ObservationValue Evaluate() => evaluator.Evaluate(expression, context);
}
