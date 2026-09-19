using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.AI.Semantics;

/// <summary>Canonical JSON interchange for the existing authored ontology model.</summary>
/// <remarks>
/// This boundary preserves rule kinds and rejects lossy typed projections. It does not resolve imports
/// or validate references: call <see cref="OntologyValidator.Validate"/> on the assembled ontology.
/// Serializer metadata is shared and immutable; no ontology content is cached.
/// </remarks>
public static class OntologyJson
{
    static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Writes the normalized ontology as canonical UTF-8 JSON text.</summary>
    /// <param name="ontology">Authored ontology; its existing constructor owns collection normalization.</param>
    /// <returns>Compact JSON with ordinal object-key ordering and explicit rule discriminators.</returns>
    /// <exception cref="ArgumentNullException">The ontology is null.</exception>
    /// <exception cref="ArgumentException">An authored value violates a model constructor invariant.</exception>
    /// <exception cref="InvalidOperationException">A rule or value cannot be normalized or represented.</exception>
    public static string SerializeCanonical(Ontology ontology)
    {
        ArgumentNullException.ThrowIfNull(ontology);
        var normalized = new Ontology(ontology.Concepts, ontology.RelationTypes, ontology.Relations, ontology.Rules);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(normalized, Options));
    }

    /// <summary>Reads a complete canonical ontology wire representation without dropping or inventing content.</summary>
    /// <param name="json">Ontology JSON produced by this contract; whitespace and object-key order may differ.</param>
    /// <returns>The normalized ontology, including every concrete rule payload.</returns>
    /// <exception cref="JsonException">
    /// The JSON is malformed, contains duplicate or unknown properties, unknown rule kinds, or changes
    /// under typed projection (including omitted defaults or normalization that discards content).
    /// </exception>
    public static Ontology Deserialize(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject<Ontology>(json, Options, "ontology", out var ontology, out var error))
            throw new JsonException($"{error.Failure}: {error.Message}", error.Location, null, null);
        return ontology!;
    }

    static JsonSerializerOptions CreateOptions()
    {
        var options = StrictDocumentJson.CreateOptions();
        options.RespectNullableAnnotations = true;
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
