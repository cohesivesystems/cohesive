namespace Cohesive.Prelude;

/// <summary>A construction-only model that can be completed by an owning composition.</summary>
public interface IAuthoringModel
{
    /// <summary>Freezes contributions and validates the declaration; repeated calls reuse the same result or failure.</summary>
    void Complete();
}
