using System.Text.Json.Serialization;

/// <summary>Typed lifecycle representation used by the load entity.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LoadStatus>))]
public enum LoadStatus
{
    /// <summary>Waiting for dispatch.</summary>
    Ready,
    /// <summary>Released to the carrier.</summary>
    Released,
    /// <summary>Cancelled by an authorized operator.</summary>
    Cancelled
}
/// <summary>A load shown in the action menu.</summary>
/// <param name="OrganizationId">Owning organization.</param>
/// <param name="Status">Lifecycle discriminator.</param>
/// <param name="DocumentsComplete">Business precondition for release.</param>
/// <param name="LastReason">Reason recorded by the last command.</param>
public sealed record Load(string OrganizationId, LoadStatus Status, bool DocumentsComplete, string LastReason);
/// <summary>Input collected before inspecting or submitting an action.</summary>
/// <param name="Reason">Operator's explanation.</param>
public sealed record Command(string Reason);
