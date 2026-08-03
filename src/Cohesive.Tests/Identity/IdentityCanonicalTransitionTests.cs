using Cohesive.Identity;
using Cohesive.Transitions.Compilation;

namespace Cohesive.Tests.Identity;

public sealed class IdentityCanonicalTransitionTests
{
    [Fact]
    public void IdentityCommands_AreCanonicalFingerprintPinnedDefinitions()
    {
        AssertCanonical(IdentityDomainModel.Scope.Rename.Compile());
        AssertCanonical(IdentityDomainModel.Scope.Archive.Compile());
        AssertCanonical(IdentityDomainModel.PrincipalAccount.Deactivate.Compile());
        AssertCanonical(IdentityDomainModel.ScopeMembership.ReplaceCapabilities.Compile());
        AssertCanonical(IdentityDomainModel.ScopeMembership.Revoke.Compile());
    }

    static void AssertCanonical(TransitionCompilationResult compilation)
    {
        Assert.True(
            compilation.IsSuccessful,
            string.Join(
                Environment.NewLine,
                compilation.Validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        Assert.Equal(compilation.Document.Metadata.DefinitionId, plan.DefinitionReference.DefinitionId);
        Assert.Equal(compilation.Document.Metadata.RevisionId, plan.DefinitionReference.RevisionId);
        Assert.Equal(compilation.Document.Metadata.Fingerprint, plan.DefinitionReference.Fingerprint);
    }
}
