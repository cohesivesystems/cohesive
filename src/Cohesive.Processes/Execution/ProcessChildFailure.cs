using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.Execution;

/// <summary>Portable structured evidence for an unhandled joined child-Process execution failure.</summary>
/// <remarks>
/// This value is a projection of the child's canonical terminal node and retained diagnostics. It is not a domain
/// error, a child result, or the semantic authority for the child terminal state. Exact child identity and terminal
/// attribution remain on the Reply origin and the child execution record.
/// </remarks>
public sealed record ProcessChildFailure
{
    /// <summary>Creates one ordered failure-evidence projection.</summary>
    /// <param name="terminalNode">Canonical child node at which the failed terminal outcome was reached.</param>
    /// <param name="diagnostics">Canonical child diagnostics in retained activation order.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="terminalNode"/> is default or <paramref name="diagnostics"/> contains a null diagnostic.
    /// </exception>
    [JsonConstructor]
    public ProcessChildFailure(
        ExecutionNodeId terminalNode,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(terminalNode.Value))
        {
            throw new ArgumentException(
                "A child execution failure requires its canonical terminal node.",
                nameof(terminalNode));
        }
        var retainedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (retainedDiagnostics.Any(static diagnostic => diagnostic is null))
        {
            throw new ArgumentException(
                "Child execution failure diagnostics cannot contain null entries.",
                nameof(diagnostics));
        }

        TerminalNode = terminalNode;
        Diagnostics = retainedDiagnostics;
    }

    /// <summary>Canonical child node at which the failed terminal outcome was reached.</summary>
    public ExecutionNodeId TerminalNode { get; }

    /// <summary>Canonical child diagnostics in retained activation order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Compares terminal-node and ordered diagnostic evidence structurally.</summary>
    public bool Equals(ProcessChildFailure? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && TerminalNode == other.TerminalNode
        && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash code for terminal-node and ordered diagnostic evidence.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TerminalNode);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        return hash.ToHashCode();
    }
}
