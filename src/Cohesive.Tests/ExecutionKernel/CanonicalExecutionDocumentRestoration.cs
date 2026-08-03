using System.Collections.Immutable;
using System.Text;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

/// <summary>Restores a canonical Process graph without retaining authoring-session or compiled state.</summary>
internal static class CanonicalExecutionDocumentRestoration
{
    internal static CompiledProcessPlan RestoreProcessPlan(
        ImmutableArray<ExecutionDefinitionDocument> sourceDocuments)
    {
        if (sourceDocuments.IsDefault)
        {
            throw new ArgumentException("Canonical source documents must be initialized.", nameof(sourceDocuments));
        }

        var documents = ImmutableArray.CreateBuilder<ExecutionDefinitionDocument>(sourceDocuments.Length);
        foreach (var document in sourceDocuments)
        {
            documents.Add(Restore(document));
        }

        var restored = documents.MoveToImmutable();
        var processDocument = Assert.Single(
            restored,
            static document => document.Kind == ProcessDefinitionDocuments.Kind);
        var transitionDocuments = restored
            .Where(static document => document.Kind == TransitionDefinitionDocuments.Kind)
            .ToArray();
        var interactionDocuments = restored
            .Where(static document => document.Kind == InteractionContractDocuments.Kind)
            .ToArray();

        var links = ImmutableArray.CreateBuilder<ProcessDefinitionLink>(transitionDocuments.Length);
        foreach (var document in transitionDocuments)
        {
            var validation = ProcessDefinitionLink.TryCreateTransition(document, out var link);
            Assert.True(validation.IsValid, Format(validation));
            links.Add(Assert.IsType<ProcessDefinitionLink>(link));
        }

        var catalogValidation = InteractionContractCatalog.TryCreate(interactionDocuments, out var catalog);
        Assert.True(catalogValidation.IsValid, Format(catalogValidation));
        var linkingContext = new ProcessDefinitionValidationContext(
            links.MoveToImmutable(),
            Assert.IsType<InteractionContractCatalog>(catalog));
        var compilation = ProcessStaticCompiler.Compile(processDocument, linkingContext);

        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        return Assert.IsType<CompiledProcessPlan>(compilation.Plan);
    }

    static ExecutionDefinitionDocument Restore(ExecutionDefinitionDocument source)
    {
        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(source);
        var json = Encoding.UTF8.GetString(canonical);
        DocumentValidationResult validation;
        ExecutionDefinitionDocument? restored;

        if (source.Kind == TransitionDefinitionDocuments.Kind)
        {
            validation = TransitionDefinitionDocuments.TryDeserialize(
                json,
                out restored,
                out _);
        }
        else if (source.Kind == ProcessDefinitionDocuments.Kind)
        {
            validation = ProcessDefinitionDocuments.TryDeserialize(
                json,
                out restored,
                out _);
        }
        else if (source.Kind == InteractionContractDocuments.Kind)
        {
            validation = InteractionContractDocuments.TryDeserialize(
                json,
                out restored,
                out _);
        }
        else
        {
            throw new InvalidOperationException(
                $"The canonical Process restoration fixture does not recognize definition kind '{source.Kind.Value}'.");
        }

        Assert.True(validation.IsValid, Format(validation));
        var document = Assert.IsType<ExecutionDefinitionDocument>(restored);
        Assert.Equal(source, document);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(document));
        Assert.Equal(source.Metadata.Fingerprint, document.Metadata.Fingerprint);
        Assert.Equal(source.Metadata.Provenance, document.Metadata.Provenance);
        Assert.Equal(source.Metadata.SourceMap, document.Metadata.SourceMap);
        Assert.Equal(source.Metadata.Diagnostics, document.Metadata.Diagnostics);
        return document;
    }

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Severity}:{diagnostic.Code}:{diagnostic.Location}:{diagnostic.Message}"));
}
