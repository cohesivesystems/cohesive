namespace AspireFirst.Orders;

/// <summary>Caller-owned fulfillment intent and retry identities; keep every field unchanged on retry.</summary>
/// <param name="CommandId">Stable command identity.</param><param name="IdempotencyKey">Stable logical admission key.</param>
/// <param name="InstanceId">Requested process instance.</param><param name="AttemptId">Initial process attempt.</param>
/// <param name="Input">The existing fulfillment process input.</param>
public sealed record FulfillmentRequest(string CommandId, string IdempotencyKey, string InstanceId,
    string AttemptId, FulfillOrder Input);

