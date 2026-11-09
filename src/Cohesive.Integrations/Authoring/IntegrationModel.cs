using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations.Authoring;

/// <summary>Application-owned integration declarations. Construct on one thread; binding a selection
/// freezes the model. Frozen declarations support independent concurrent compilations.</summary>
/// <typeparam name="TSelection">Application-owned structural selection source.</typeparam>
public abstract class IntegrationModel<TSelection> where TSelection : class
{
    internal IntegrationAuthoringContext Context { get; }

    /// <summary>Creates a declaration namespace and explicit semantic revision.</summary>
    /// <param name="id">Stable model identity.</param>
    /// <param name="revision">Explicit semantic revision.</param>
    /// <exception cref="ArgumentException">Identity or revision is empty.</exception>
    protected IntegrationModel(string id, string revision) => Context = new(id, revision);

    /// <summary>Overrides the default CLR-full-name payload identity convention.</summary>
    /// <typeparam name="T">Payload source type.</typeparam>
    /// <param name="id">Stable application-owned payload identity within the model.</param>
    /// <param name="revision">Schema revision; defaults to the model revision.</param>
    /// <exception cref="InvalidOperationException">The model is frozen or the type already has a mapping.</exception>
    /// <exception cref="ArgumentException">An identity is empty.</exception>
    protected void Payload<T>(string id, string? revision = null) => Context.Payload<T>(id, revision);

    /// <summary>Starts a typed Request declaration.</summary>
    /// <typeparam name="TInput">Request payload source type.</typeparam>
    /// <param name="name">Stable local identity.</param>
    /// <returns>A builder requiring one successful result.</returns>
    /// <exception cref="ArgumentException">The name is empty or already declared.</exception>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    protected RequestBuilder<TInput> Request<TInput>(string name)
    {
        Context.Reserve(name);
        Context.Requests.Add(name, null);
        return new(Context, name);
    }

    /// <summary>Declares reusable response recovery policy.</summary>
    /// <param name="name">Stable local source identity.</param>
    /// <returns>A policy builder requiring explicit retry, reconciliation, duplicate and retention choices.</returns>
    /// <exception cref="ArgumentException">The name is empty or already declared.</exception>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    protected IntegrationRecovery Recovery(string name)
    {
        Context.Reserve(name);
        return new(Context);
    }

    /// <summary>Declares a scoped property over the model selection.</summary>
    /// <param name="name">Stable local identity.</param>
    /// <returns>A property builder that lowers to the shared Contract property definition.</returns>
    /// <exception cref="ArgumentException">The name is empty or already declared.</exception>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    protected PropertyHandle<TSelection> Property(string name)
    {
        Context.Reserve(name);
        return new(Context, name);
    }

    /// <summary>Declares a bounded ingestion interaction.</summary>
    /// <param name="name">Stable local identity.</param>
    /// <returns>A builder for acquire, publish, optional progress advancement and settlement.</returns>
    /// <exception cref="ArgumentException">The name is empty or already declared.</exception>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    protected IngestionHandle<TSelection> Ingestion(string name)
    {
        Context.Reserve(name);
        return new(Context, name);
    }
    /// <summary>Authors requirements and binding around an existing exact ingestion protocol.</summary>
    /// <param name="name">Local source handle identity.</param>
    /// <param name="definition">Pinned ingestion declaration; its Request identities and recovery policy are preserved.</param>
    /// <param name="dependencies">Materialized dependency closure, including its Request and Reply documents.</param>
    /// <returns>A handle whose protocol is fixed and whose contract properties remain authorable.</returns>
    /// <exception cref="ArgumentException">The name is invalid or the linked protocol fails validation.</exception>
    /// <exception cref="ArgumentNullException">Definition or dependencies is null.</exception>
    /// <exception cref="InvalidOperationException">The model is frozen.</exception>
    protected IngestionHandle<TSelection> Ingestion(string name, ExecutionDefinitionDocument definition,
        IReadOnlyCollection<ExecutionDefinitionDocument> dependencies)
    {
        Context.Reserve(name);
        return IngestionHandle<TSelection>.Import(Context, name, definition, dependencies);
    }

}

// Owns source identities and lifecycle only. Canonical documents and native validators own semantics.
internal sealed class IntegrationAuthoringContext
{
    readonly HashSet<string> names = new(StringComparer.Ordinal);
    readonly Dictionary<Type, InteractionValueSchemaRevision> payloads = [];
    bool frozen;
    internal string Id { get; }
    internal ExecutionRevisionId Revision { get; }
    internal Dictionary<string, IntegrationRequest?> Requests { get; } = new(StringComparer.Ordinal);

    internal IntegrationAuthoringContext(string id, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Revision = new(revision);
    }

    internal void Mutable()
    {
        if (frozen) throw new InvalidOperationException("The integration model is frozen. Create a new model to author another revision.");
    }

    internal void Freeze() => frozen = true;

    internal void Reserve(string name)
    {
        Mutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!names.Add(name)) throw new ArgumentException($"Integration declaration '{name}' already exists.", nameof(name));
    }

    internal void Payload<T>(string id, string? revision)
    {
        Mutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (revision is not null) ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        var value = new InteractionValueSchemaRevision(Id + "/payload/" + Uri.EscapeDataString(id) + "/" + (revision ?? Revision.Value));
        if (!payloads.TryAdd(typeof(T), value)) throw new InvalidOperationException("A payload mapping is already declared for this type.");
    }

    internal InteractionValueSchemaRevision Schema<T>() => payloads.TryGetValue(typeof(T), out var revision)
        ? revision : new(Id + "/payload/" + Uri.EscapeDataString(typeof(T).FullName!) + "/" + Revision.Value);

    internal ExecutionDefinitionId Identity(string local) => new(Id + "/" + local);
    internal ExecutionProvenance Origin(string local) => new(new("cohesive.integrations.authoring", "1"),
        new("integration-model:" + Id + "/" + local), DocumentOrigin.Generated);

    internal void Owns(IntegrationAuthoringContext other)
    {
        if (!ReferenceEquals(this, other)) throw new ArgumentException("Handles must belong to the same integration model.");
    }

    internal static DocumentValidationResult Error(string message) => new([
        new("integrations.authoring.invalid", DiagnosticSeverity.Error, message, "/authoring")]);
}

/// <summary>Canonical compilation bundle. Successful compilation leaves physical qualification explicit.</summary>
/// <param name="Documents">Complete Request, Reply, property, context, provider, binding and Process closure.</param>
/// <param name="Binding">Compiled provider binding, available only on success.</param>
/// <param name="Process">Compiled ingestion Process, available only on success.</param>
/// <param name="Validation">Shared compiler diagnostics, including unqualified realization warnings.</param>
/// <param name="Qualification">Optional exact capability admission plan; runtime evidence can still be pending.</param>
public sealed record IntegrationCompilation(ImmutableArray<ExecutionDefinitionDocument> Documents,
    ExecutionDefinitionDocument? Binding, ExecutionDefinitionDocument? Process, DocumentValidationResult Validation, ExecutionDefinitionDocument? Qualification = null)
{
    /// <summary>Whether structural compilation succeeded.</summary>
    public bool IsSuccessful => Validation.IsValid && Binding is not null && Process is not null;
}
