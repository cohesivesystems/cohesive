namespace Cohesive.Storage.Processes;

/// <summary>An exact reaction operation or receipt conflicts with the configured protocol binding.</summary>
/// <remarks>This describes a protocol mismatch, not evidence that a target did not commit.</remarks>
public sealed class MachineReactionProtocolException : InvalidOperationException
{
    /// <summary>The requested transition revision differs from the configured target.</summary>
    public const string RevisionMismatch = "machine.reaction.protocol.revisionMismatch";
    /// <summary>The resolved receipt does not prove the requested operation.</summary>
    public const string ReceiptMismatch = "machine.reaction.protocol.receiptMismatch";

    /// <summary>Creates a structured protocol conflict.</summary>
    /// <param name="code">Stable protocol diagnostic code.</param>
    /// <param name="message">Description without sensitive receipt contents.</param>
    /// <exception cref="ArgumentException">The code or message is empty.</exception>
    public MachineReactionProtocolException(string code, string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
    }

    /// <summary>Stable machine-readable diagnostic.</summary>
    public string Code { get; }
}
