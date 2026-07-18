using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Versioned defaults for expression-authored relation identity, display name, provenance reference, and root
/// selection.
/// </summary>
/// <remarks>
/// The relation identity is derived from the graph-qualified root and output shapes plus the output mode. It does
/// not include the current body, key, or invariants, allowing those semantics to evolve under one durable relation
/// identity. Consumers authoring more than one relation for the same endpoint pair and output mode must use the
/// explicit terminal overload.
///
/// <para>
/// Version 1 hashes, in order, <see cref="Version"/>, the root graph ID, root shape ID, output graph ID, output
/// shape ID, and the invariant decimal integer value of the output mode. Each field is encoded as its invariant
/// decimal UTF-8 byte count, a colon, the unnormalized UTF-8 bytes, and a semicolon. The lower-case SHA-256 digest
/// is appended to <see cref="IdPrefix"/>.
/// </para>
/// </remarks>
public static class RelationQueryExpressionRelationConvention
{
    /// <summary>Canonical convention version encoded into identities and retained by default-value provenance.</summary>
    public const string Version = "relation-query-expression-relation/v1";

    /// <summary>Prefix used for relation identities produced by this convention.</summary>
    public const string IdPrefix = "relation:v1:sha256:";

    /// <summary>Stable configuration-setting identity for the effective relation display name.</summary>
    public const string NameSetting = "relation/name";

    /// <summary>Stable configuration-setting identity for the effective relation source reference.</summary>
    public const string SourceReferenceSetting = "relation/source-reference";

    /// <summary>Stable configuration-setting identity for a convention-selected relation-root binding.</summary>
    public const string RootBindingSetting = "relation/root-binding";

    /// <summary>Creates a deterministic relation identity from its semantic endpoint shapes and output mode.</summary>
    /// <param name="rootShape">Graph-qualified relation-root shape.</param>
    /// <param name="outputShape">Graph-qualified relation-output shape.</param>
    /// <param name="mode">Output cardinality relative to each root.</param>
    /// <returns>A versioned SHA-256 relation identity.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="rootShape"/> or <paramref name="outputShape"/> is default or incomplete.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unsupported.</exception>
    public static RelationId CreateId(
        QualifiedShapeId rootShape,
        QualifiedShapeId outputShape,
        RelationOutputMode mode = RelationOutputMode.OnePerRoot)
    {
        RequireShape(rootShape, nameof(rootShape));
        RequireShape(outputShape, nameof(outputShape));
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported relation output mode.");
        }

        ArrayBufferWriter<byte> canonical = new();
        Append(canonical, Version);
        Append(canonical, rootShape.GraphId.Value);
        Append(canonical, rootShape.ShapeId.Value);
        Append(canonical, outputShape.GraphId.Value);
        Append(canonical, outputShape.ShapeId.Value);
        Append(canonical, ((int)mode).ToString(CultureInfo.InvariantCulture));
        return new RelationId(IdPrefix + Convert.ToHexStringLower(SHA256.HashData(canonical.WrittenSpan)));
    }

    /// <summary>Creates the default human-readable relation name from its CLR output type.</summary>
    /// <param name="outputType">CLR type produced by the relation.</param>
    /// <returns>The CLR type's simple name, without generic arity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="outputType"/> is <see langword="null"/>.</exception>
    public static RelationName CreateName(Type outputType)
    {
        ArgumentNullException.ThrowIfNull(outputType);
        var normalized = Nullable.GetUnderlyingType(outputType) ?? outputType;
        var name = normalized.Name;
        var arity = name.IndexOf('`');
        return new RelationName(arity < 0 ? name : name[..arity]);
    }

    static void RequireShape(QualifiedShapeId shape, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
        {
            throw new ArgumentException("A graph-qualified semantic shape is required.", parameterName);
        }
    }

    static void Append(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteAscii(writer, byteCount.ToString(CultureInfo.InvariantCulture));
        WriteByte(writer, (byte)':');
        var destination = writer.GetSpan(byteCount);
        writer.Advance(Encoding.UTF8.GetBytes(value, destination));
        WriteByte(writer, (byte)';');
    }

    static void WriteAscii(ArrayBufferWriter<byte> writer, string value)
    {
        var destination = writer.GetSpan(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            destination[index] = (byte)value[index];
        }

        writer.Advance(value.Length);
    }

    static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }
}
