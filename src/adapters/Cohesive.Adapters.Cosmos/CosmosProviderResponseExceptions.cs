using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Reports that a completed Cosmos response violated the adapter's provider protocol before its payload could be
/// admitted into Cohesive semantics.
/// </summary>
internal sealed class CosmosProviderProtocolException : InvalidOperationException
{
    const string EvidenceProfile = "cosmos-provider-protocol/v1";

    /// <summary>Creates a sanitized provider-protocol failure.</summary>
    /// <param name="reason">Stable non-sensitive reason code.</param>
    /// <param name="message">Non-sensitive diagnostic message.</param>
    /// <param name="statusCode">HTTP status from the completed response, when one was available.</param>
    /// <param name="requestCharge">
    /// Finite non-negative request charge from the completed response, or <see langword="null"/> when unavailable or
    /// invalid.
    /// </param>
    /// <param name="providerActivityId">
    /// Provider activity identifier used only to derive non-sensitive evidence; the identifier itself is not retained.
    /// </param>
    /// <param name="responseChargeAccounted">
    /// Whether an owning completed-response accumulator already observed <paramref name="requestCharge"/>.
    /// </param>
    internal CosmosProviderProtocolException(
        string reason,
        string message,
        HttpStatusCode? statusCode = null,
        double? requestCharge = null,
        string? providerActivityId = null,
        bool responseChargeAccounted = false)
        : base(message)
    {
        Reason = Guard.RequireNotNullOrWhiteSpace(reason);
        StatusCode = statusCode;
        RequestCharge = requestCharge is { } charge && double.IsFinite(charge) && charge >= 0
            ? charge
            : null;
        ResponseChargeAccounted = responseChargeAccounted && RequestCharge is not null;
        ProviderEvidenceReference = CreateEvidenceReference(
            Reason,
            StatusCode,
            RequestCharge,
            providerActivityId,
            ResponseChargeAccounted);
    }

    /// <summary>Stable non-sensitive provider-protocol reason.</summary>
    internal string Reason { get; }

    /// <summary>HTTP status from the completed response, when available.</summary>
    internal HttpStatusCode? StatusCode { get; }

    /// <summary>Finite non-negative charge from the completed response, when available.</summary>
    internal double? RequestCharge { get; }

    /// <summary>
    /// Whether an owning completed-response accumulator successfully observed <see cref="RequestCharge"/> exactly
    /// once. When <see langword="true"/>, a catch boundary must not add the charge again; when
    /// <see langword="false"/>, the charge has not been reported through that accumulator.
    /// </summary>
    internal bool ResponseChargeAccounted { get; }

    /// <summary>Opaque evidence derived from sanitized response facts and a hash of the provider activity identifier.</summary>
    internal string ProviderEvidenceReference { get; }

    /// <summary>Creates opaque evidence without retaining the provider activity identifier.</summary>
    /// <param name="reason">Stable non-sensitive evidence reason.</param>
    /// <param name="statusCode">Completed response status, when available.</param>
    /// <param name="requestCharge">Validated completed response charge, when available.</param>
    /// <param name="providerActivityId">Provider activity identifier hashed into the result.</param>
    /// <param name="responseChargeAccounted">Whether the charge was already recorded by an owning accumulator.</param>
    /// <returns>An opaque deterministic evidence reference.</returns>
    internal static string CreateEvidenceReference(
        string reason,
        HttpStatusCode? statusCode,
        double? requestCharge,
        string? providerActivityId,
        bool responseChargeAccounted)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, EvidenceProfile);
        Append(hash, reason);
        Append(hash, statusCode is { } status
            ? ((int)status).ToString(CultureInfo.InvariantCulture)
            : null);
        Append(hash, requestCharge?.ToString("R", CultureInfo.InvariantCulture));
        Append(hash, responseChargeAccounted ? "accounted" : "unaccounted");
        if (!string.IsNullOrWhiteSpace(providerActivityId))
        {
            Append(hash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(providerActivityId))));
        }
        else
        {
            Append(hash, null);
        }

        return $"{EvidenceProfile}/sha256/{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    static void Append(IncrementalHash hash, string? value)
    {
        var framed = string.Concat(
            value?.Length.ToString(CultureInfo.InvariantCulture) ?? "-1",
            ":",
            value,
            ";");
        hash.AppendData(Encoding.UTF8.GetBytes(framed));
    }
}

/// <summary>
/// Reports caller cancellation observed after Cosmos completed a response, retaining its sanitized operational
/// evidence without admitting a partial provider page.
/// </summary>
internal sealed class CosmosProviderResponseCanceledException : OperationCanceledException
{
    /// <summary>Creates a completed-response cancellation.</summary>
    /// <param name="statusCode">HTTP status from the completed response.</param>
    /// <param name="requestCharge">Finite non-negative request charge from the completed response.</param>
    /// <param name="providerActivityId">
    /// Provider activity identifier used only to derive non-sensitive evidence; the identifier itself is not retained.
    /// </param>
    /// <param name="cancellationToken">Canceled caller token.</param>
    /// <param name="responseChargeAccounted">
    /// Whether an owning completed-response accumulator already observed <paramref name="requestCharge"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="requestCharge"/> is negative or non-finite.
    /// </exception>
    internal CosmosProviderResponseCanceledException(
        HttpStatusCode statusCode,
        double requestCharge,
        string? providerActivityId,
        CancellationToken cancellationToken,
        bool responseChargeAccounted = false)
        : base(
            "Caller cancellation was observed after Cosmos completed a response; inspect typed response evidence.",
            innerException: null,
            cancellationToken)
    {
        if (!double.IsFinite(requestCharge) || requestCharge < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestCharge),
                requestCharge,
                "A completed Cosmos response charge must be finite and non-negative.");
        }

        StatusCode = statusCode;
        RequestCharge = requestCharge;
        ResponseChargeAccounted = responseChargeAccounted;
        ProviderEvidenceReference = CosmosProviderProtocolException.CreateEvidenceReference(
            "completed-response-canceled",
            statusCode,
            requestCharge,
            providerActivityId,
            responseChargeAccounted);
    }

    /// <summary>HTTP status from the completed response.</summary>
    internal HttpStatusCode StatusCode { get; }

    /// <summary>Request-unit charge from the completed response.</summary>
    internal double RequestCharge { get; }

    /// <summary>
    /// Whether an owning completed-response accumulator successfully observed <see cref="RequestCharge"/> exactly
    /// once. When <see langword="true"/>, a catch boundary must not add the charge again; when
    /// <see langword="false"/>, the charge has not been reported through that accumulator.
    /// </summary>
    internal bool ResponseChargeAccounted { get; }

    /// <summary>Opaque evidence derived from sanitized response facts and a hash of the provider activity identifier.</summary>
    internal string ProviderEvidenceReference { get; }
}

/// <summary>Classifies exceptions that can be safely normalized at a provider response boundary.</summary>
internal static class CosmosProviderExceptionBoundary
{
    /// <summary>Determines whether an exception should become a sanitized provider-protocol failure.</summary>
    /// <param name="exception">Exception raised while obtaining or projecting a provider response.</param>
    /// <param name="cancellationToken">Caller cancellation token for the operation.</param>
    /// <returns>
    /// <see langword="true"/> for non-fatal, non-Cosmos failures that do not represent caller cancellation;
    /// otherwise <see langword="false"/>.
    /// </returns>
    internal static bool ShouldNormalize(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        CosmosProviderProtocolException => false,
        CosmosProviderResponseCanceledException => false,
        Microsoft.Azure.Cosmos.CosmosException => false,
        OperationCanceledException when cancellationToken.IsCancellationRequested => false,
        OutOfMemoryException or StackOverflowException or AccessViolationException => false,
        _ => true
    };
}
