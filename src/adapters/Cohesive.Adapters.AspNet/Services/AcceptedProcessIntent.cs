using System.Security.Cryptography;
using System.Text;
using Cohesive.Execution;
using Cohesive.Model;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Services;

/// <summary>Standard retry identity conventions for accepted HTTP submissions.</summary>
public static class AcceptedProcessIntent
{
    /// <summary>Derives stable command, idempotency and first-attempt identities from one public request ID.</summary>
    /// <remarks>The same exact UTF-8 request ID replays in the same admission scope. Payload conflicts remain visible.</remarks>
    public static (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency,
        ProcessContinuationIdentity Continuation, ObservationValue Input) FromRequestId(string requestId, ObservationValue input)
    {
        if (string.IsNullOrWhiteSpace(requestId)) throw new BadHttpRequestException("A request ID is required.");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId)));
        return (new(id), new(id), new(new(id), new("attempt/1")), input);
    }
}
