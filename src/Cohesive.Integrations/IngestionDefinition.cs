using Cohesive.Execution;

namespace Cohesive.Integrations;

/// <summary>One bounded acquire, atomic application, and source settlement composition.</summary>
/// <param name="Acquire">Exact Request acquiring complete, replayable work; its payload owns range or cursor semantics.</param>
/// <param name="Publish">Exact Request atomically accepting destination effects, application coverage, and operation evidence.</param>
/// <param name="Settle">Exact Request settling source work after publication; its payload receives publication evidence.</param>
/// <remarks>
/// The surrounding execution document identifies and versions the application binding. Each Request has one
/// successful result; its result schema and revision must equal the following Request's payload contract. Payloads must carry
/// the work, source/destination scope, and settlement references needed by the next step. Their exact contracts are
/// the authority, not a second generic ingestion envelope. Publication includes normalization/validation or invokes
/// an application Process that performs them. Separate ledger commits and early acknowledgment are outside this
/// initial profile. Request declarations are requirements, not proof that a physical adapter preserves them.
/// </remarks>
public sealed record IngestionDefinition(
    RequestContractReference Acquire,
    RequestContractReference Publish,
    RequestContractReference Settle);
