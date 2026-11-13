using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>Authoring-time scope for explicit content-addressed fixture declarations and their reference catalog.</summary>
/// <remarks>
/// Intended for tests, examples and documentation. A fingerprint identifies the exact descriptive text,
/// not a compiled entity, selector, policy or observation adapter. Use owning-model references for real definitions.
/// A scope is single-threaded authoring state; returned references and catalog snapshots are immutable.
/// </remarks>
public sealed class ClaimReferenceFixture
{
    const string Profile = "cohesive-assurance-reference-fixture/v1";
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();
    readonly Dictionary<string, (string Definition, ClaimReference Reference)> declarations = new(StringComparer.Ordinal);

    internal ClaimReferenceFixture(string sourceRoot, string revision)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        SourceRoot = new SourceReference(sourceRoot.TrimEnd('/'));
        if (SourceRoot.Value.AsSpan().IndexOfAny("?#%\\") >= 0)
            throw new ArgumentException("A fixture root cannot contain query, fragment, percent escapes or backslashes.", nameof(sourceRoot));
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
    }

    /// <summary>Validated source namespace shared by the declarations.</summary>
    public SourceReference SourceRoot { get; }
    /// <summary>Explicit immutable revision shared by this fixture.</summary>
    public string Revision { get; }
    /// <summary>Canonical immutable snapshot of declarations so far, suitable for ClaimDocument.ValidateReferences.</summary>
    public ImmutableArray<ClaimReference> References => [.. declarations.Values.Select(x => x.Reference)
        .OrderBy(x => x, ClaimReference.CanonicalComparer)];

    /// <summary>Declares a fixture reference and records it in this scope's catalog.</summary>
    /// <param name="path">Relative slash-separated identity with no empty, dot, parent, query, fragment or escaped segments.</param>
    /// <param name="definition">Exact nonempty declaration text; whitespace and line endings are significant.</param>
    /// <returns>Reference with shared source root/revision and a versioned canonical content fingerprint.</returns>
    /// <exception cref="ArgumentNullException">Path or definition is null.</exception>
    /// <exception cref="ArgumentException">Path or definition is empty, path is noncanonical, or a path is redeclared with different content.</exception>
    public ClaimReference Define(string path, string definition)
    {
        path = Guard.RequireNotNullOrWhiteSpace(path);
        definition = Guard.RequireNotNullOrWhiteSpace(definition);
        if (path.AsSpan().IndexOfAny(":?#%\\") >= 0 || path.Split('/').Any(x => x.Length == 0 || x is "." or ".."))
            throw new ArgumentException("Use a canonical relative slash-separated fixture path.", nameof(path));
        if (declarations.TryGetValue(path, out var existing))
        {
            if (!string.Equals(existing.Definition, definition, StringComparison.Ordinal))
                throw new ArgumentException("This fixture path already has different content; use a new fixture revision or a distinct path.", nameof(path));
            return existing.Reference;
        }
        var source = new SourceReference(SourceRoot.Value + "/" + path);
        var digest = Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(new Declaration(Profile, definition), Options));
        var reference = new ClaimReference(source, Revision, FingerprintText.Format("sha256", Profile, digest));
        declarations.Add(path, (definition, reference));
        return reference;
    }

    sealed record Declaration(string Schema, string Definition);
}
