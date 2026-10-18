using Cohesive.Prelude;
using Cohesive.Infra.Realization;

namespace Cohesive.Infra;

/// <summary>Constructor-authored infrastructure using the existing coordinated definition builder.</summary>
/// <remarks>Native resource construction is a separate explicit adapter operation. Activation creates no cloud resources.</remarks>
public abstract class InfrastructureModel : IAuthoringModel
{
    Func<InfrastructureAuthoringResult>? define;
    readonly AuthoringState<InfrastructureAuthoringResult> state;
    /// <summary>Starts one offline declaration lifetime.</summary>
    protected InfrastructureModel() => state = new(() => (define ?? throw new InvalidOperationException("Declare infrastructure before preparation."))());
    /// <summary>Records one canonical authoring recipe during construction.</summary>
    /// <param name="id">Stable definition identity.</param><param name="revision">Explicit revision.</param>
    /// <param name="bindingProfileId">Canonical binding profile.</param><param name="configure">Existing native-independent builder recipe.</param>
    protected void Define(InfrastructureDefinitionId id, InfrastructureRevisionId revision,
        InfrastructureBindingElaborationProfileId bindingProfileId, Action<InfrastructureDefinitionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        state.Edit(() =>
        {
            if (define is not null) throw new InvalidOperationException("Infrastructure is already declared.");
            define = () => Infrastructure.Define(id, revision, bindingProfileId, configure);
            return this;
        });
    }
    /// <summary>Cached canonical definition and binding profile, identical to direct Infrastructure.Define authoring.</summary>
    public InfrastructureAuthoringResult Definition => state.Value;
    void IAuthoringModel.Complete() => _ = Definition;
}
