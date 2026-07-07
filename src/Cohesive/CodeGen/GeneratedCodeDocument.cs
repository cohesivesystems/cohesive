using Cohesive.Prelude;

namespace Cohesive.CodeGen;

/// <summary>
/// Generated source document.
/// </summary>
public sealed record GeneratedCodeDocument
{
    /// <summary>
    /// Creates a generated document.
    /// </summary>
    public GeneratedCodeDocument(string fileName, string text)
    {
        FileName = Guard.RequireNotNullOrWhiteSpace(fileName);
        Text = Guard.RequireNotNull(text);
    }

    /// <summary>
    /// Output file name.
    /// </summary>
    public string FileName { get; init; }

    /// <summary>
    /// Generated source text.
    /// </summary>
    public string Text { get; init; }
}
