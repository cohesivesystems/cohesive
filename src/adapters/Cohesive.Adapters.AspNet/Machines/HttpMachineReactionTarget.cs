using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Prelude;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;

namespace Cohesive.Adapters.AspNet.Machines;

/// <summary>Dispatches exact Process operations over HTTP and resolves their retained entity receipts.</summary>
/// <remarks>The caller owns HTTP authentication and client lifetime. This profile requires string entity
/// subjects. Transport acceptance is never commitment evidence; only a matching receipt completes dispatch.</remarks>
public sealed class HttpMachineReactionTarget : MachineReactionTarget
{
    internal static readonly JsonSerializerOptions Json = ProcessDurableCheckpointJsonSerializer.CreateOptions();
    internal const string ReceiptPath = "receipt";
    readonly HttpClient client;
    readonly Uri endpoint;
    readonly Uri receiptEndpoint;

    /// <summary>Binds an exact transition and authority to a trusted HTTP service.</summary>
    /// <param name="transition">Exact remote transition revision.</param>
    /// <param name="authority">Remote authority and tenant.</param>
    /// <param name="client">Caller-owned authenticated HTTP client.</param>
    /// <param name="endpoint">Absolute execution endpoint, without query or fragment.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The endpoint is not an absolute HTTP URL.</exception>
    public HttpMachineReactionTarget(ExecutionDefinitionReference transition, InteractionAuthorityScope authority,
        HttpClient client, Uri endpoint) : base(transition, authority)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https")
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("An absolute HTTP endpoint without query or fragment is required.", nameof(endpoint));
        this.endpoint = endpoint;
        receiptEndpoint = new(endpoint.AbsoluteUri.TrimEnd('/') + "/" + ReceiptPath);
    }

    /// <inheritdoc />
    /// <exception cref="HttpRequestException">The transport fails or the remote service returns a failure status; commitment may be unknown.</exception>
    /// <exception cref="InvalidOperationException">The invocation or receipt differs from this binding, or the response is only acceptance.</exception>
    /// <exception cref="JsonException">The response is not a valid canonical receipt.</exception>
    public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
    {
        if (invocation.Definition != Transition || invocation.Context.AuthorityScope != Authority)
            throw new InvalidOperationException("Invocation differs from the remote target binding.");
        var subject = invocation.Subject.Value?.GetString();
        if (string.IsNullOrWhiteSpace(subject)) throw new InvalidOperationException("HTTP target requires a string entity identity.");
        using var response = await client.PostAsJsonAsync(endpoint, invocation, Json, context.CancellationToken).ConfigureAwait(false);
        var receipt = await ReadReceipt(response, context).ConfigureAwait(false);
        var request = receipt.Request;
        if (request.Transition != Transition || request.AuthorityScope != Authority
            || request.Input != invocation.Input || request.Subject.EntityId.Value != subject
            || request.Operation != new ProcessOperationOccurrence(invocation.Continuation, invocation.Activation,
                invocation.Token, invocation.Node, invocation.Occurrence))
            throw new InvalidOperationException("HTTP response does not prove the requested operation.");
        return receipt.Commit.Result.WithReceiptReference(EntityTransitionReceiptReferences.Project(request.Reference));
    }

    /// <inheritdoc />
    /// <exception cref="HttpRequestException">Receipt resolution fails at the transport or service boundary.</exception>
    /// <exception cref="InvalidOperationException">The service returns acceptance or a receipt for another operation.</exception>
    /// <exception cref="JsonException">The response is not a valid canonical receipt.</exception>
    public override async ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
    {
        if (reference.AuthorityScope != Authority) throw new UnauthorizedAccessException("Receipt scope differs from target authority.");
        using var response = await client.PostAsJsonAsync(receiptEndpoint, reference, Json, context.CancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var receipt = await ReadReceipt(response, context).ConfigureAwait(false);
        if (receipt.Request.Reference != reference || receipt.Request.Transition != Transition)
            throw new InvalidOperationException("HTTP receipt differs from the requested locator.");
        return receipt;
    }

    static async Task<EntityTransitionOperationReceipt> ReadReceipt(HttpResponseMessage response, OperationContext context)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("HTTP acceptance is not an operation receipt.");
        return await response.Content.ReadFromJsonAsync<EntityTransitionOperationReceipt>(Json, context.CancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("HTTP target omitted its operation receipt.");
    }
}
