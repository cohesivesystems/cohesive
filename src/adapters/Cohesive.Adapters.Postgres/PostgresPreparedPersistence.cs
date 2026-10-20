namespace Cohesive.Adapters.Postgres;

/// <summary>Prepared repositories and model-declared attachments borrowing caller-owned native clients.</summary>
/// <param name="Persistence">Repository and query registration.</param>
/// <param name="Receipts">Validated receipts, or null when undeclared.</param>
/// <param name="History">Validated history, or null when undeclared.</param>
/// <param name="Outbox">Validated outbox, or null when undeclared.</param>
public sealed record PostgresPreparedPersistence(PostgresPersistenceRegistration Persistence,
    PostgresTransitionReceiptStorage? Receipts, PostgresEntityHistoryStorage? History, PostgresEntityOutboxStorage? Outbox);
