using System.Collections.Immutable;
using Cohesive.Prelude;

namespace Cohesive.CodeGen;

/// <summary>
/// Result of running a code emitter.
/// </summary>
public sealed record CodeEmission
{
    /// <summary>
    /// Creates an emission result.
    /// </summary>
    public CodeEmission(string language, ImmutableArray<GeneratedCodeDocument> documents)
    {
        Language = Guard.RequireNotNullOrWhiteSpace(language);
        Documents = documents.IsDefault ? [] : documents;
    }

    /// <summary>
    /// Output language identifier.
    /// </summary>
    public string Language { get; init; }

    /// <summary>
    /// Generated documents.
    /// </summary>
    public ImmutableArray<GeneratedCodeDocument> Documents { get; init; }
}
