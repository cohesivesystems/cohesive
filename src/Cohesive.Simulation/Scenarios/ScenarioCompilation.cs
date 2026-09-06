using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Scenarios;

/// <summary>Immutable provider-neutral plan for one exact deterministic scenario.</summary>
public sealed class CompiledScenarioPlan
{
    internal CompiledScenarioPlan(ScenarioDefinition definition, string fingerprint)
    {
        Definition = definition;
        Fingerprint = Guard.RequireNotNullOrWhiteSpace(fingerprint);
    }

    /// <summary>Gets the normalized canonical scenario definition.</summary>
    public ScenarioDefinition Definition { get; }

    /// <summary>Gets the lowercase SHA-256 fingerprint of exact scenario semantics.</summary>
    public string Fingerprint { get; }

    /// <summary>Gets the fingerprint algorithm identity.</summary>
    public string FingerprintAlgorithm => ScenarioDefinitionFingerprint.CurrentAlgorithm;

    /// <summary>Gets the canonicalization profile used by <see cref="Fingerprint"/>.</summary>
    public string FingerprintCanonicalization => ScenarioDefinitionFingerprint.CurrentCanonicalization;

    /// <summary>Finds one operation contract by stable identity.</summary>
    /// <param name="id">Stable operation identity.</param>
    /// <param name="operation">Receives the operation when found.</param>
    /// <returns><see langword="true"/> when the operation exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public bool TryGetOperation(string id, out ScenarioOperationDefinition? operation)
    {
        id = Guard.RequireNotNullOrWhiteSpace(id);
        foreach (var candidate in Definition.Operations)
        {
            if (!string.Equals(candidate.Id, id, StringComparison.Ordinal))
                continue;

            operation = candidate;
            return true;
        }

        operation = null;
        return false;
    }

    /// <summary>Gets one operation contract by stable identity.</summary>
    /// <param name="id">Stable operation identity.</param>
    /// <returns>The operation named by <paramref name="id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">No operation has the supplied identity.</exception>
    public ScenarioOperationDefinition GetOperation(string id) =>
        TryGetOperation(id, out var operation)
            ? operation!
            : throw new KeyNotFoundException(
                $"Scenario '{Definition.Id}' contains no operation with identity '{id}'.");

    /// <summary>Finds one actor by stable identity.</summary>
    /// <param name="id">Stable actor identity.</param>
    /// <param name="actor">Receives the actor when found.</param>
    /// <returns><see langword="true"/> when the actor exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public bool TryGetActor(string id, out ScenarioActorDefinition? actor)
    {
        id = Guard.RequireNotNullOrWhiteSpace(id);
        foreach (var candidate in Definition.Actors)
        {
            if (!string.Equals(candidate.Id, id, StringComparison.Ordinal))
                continue;

            actor = candidate;
            return true;
        }

        actor = null;
        return false;
    }

    /// <summary>Gets one actor by stable identity.</summary>
    /// <param name="id">Stable actor identity.</param>
    /// <returns>The actor named by <paramref name="id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">No actor has the supplied identity.</exception>
    public ScenarioActorDefinition GetActor(string id) =>
        TryGetActor(id, out var actor)
            ? actor!
            : throw new KeyNotFoundException(
                $"Scenario '{Definition.Id}' contains no actor with identity '{id}'.");
}

/// <summary>Result of attempting provider-neutral scenario compilation.</summary>
public sealed class ScenarioCompilationResult
{
    internal ScenarioCompilationResult(
        ScenarioDefinition definition,
        CompiledScenarioPlan? plan,
        DocumentValidationResult validation)
    {
        Definition = definition;
        Plan = plan;
        Validation = validation;
    }

    /// <summary>Gets the exact supplied scenario definition.</summary>
    public ScenarioDefinition Definition { get; }

    /// <summary>Gets a compiled scenario only when validation succeeds.</summary>
    public CompiledScenarioPlan? Plan { get; }

    /// <summary>Gets deterministically ordered structured diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Gets whether compilation produced a complete scenario plan.</summary>
    public bool IsSuccessful => Plan is not null && Validation.IsValid;
}

/// <summary>Failure raised when convenience scenario compilation encounters structured diagnostics.</summary>
public sealed class ScenarioCompilationException : InvalidOperationException
{
    /// <summary>Creates a scenario compilation exception.</summary>
    /// <param name="validation">Structured validation evidence explaining the failure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    public ScenarioCompilationException(DocumentValidationResult validation)
        : base(CreateMessage(validation)) => Validation = validation;

    /// <summary>Gets structured scenario-validation evidence.</summary>
    public DocumentValidationResult Validation { get; }

    static string CreateMessage(DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        var errors = validation.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}");
        return $"Scenario definition could not be compiled: {string.Join(" | ", errors)}";
    }
}

/// <summary>Compiles portable scenario IR into a normalized deterministic schedule.</summary>
public static class ScenarioCompiler
{
    /// <summary>Compiles and validates one canonical scenario definition.</summary>
    /// <param name="definition">Scenario definition to compile.</param>
    /// <returns>A result containing either a complete plan or precise structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static ScenarioCompilationResult Compile(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DocumentValidationDiagnostic> diagnostics = [];

        if (definition.StartsAtUtc.Offset != TimeSpan.Zero)
        {
            Add(
                diagnostics,
                code: "simulation.scenario.startTimeNotUtc",
                message: "A scenario virtual-time origin must use the UTC offset.",
                location: "/startsAtUtc");
        }

        var operations = ValidateOperations(definition, diagnostics);
        var actors = ValidateActors(definition, diagnostics);
        var actions = ValidateActions(definition, operations, actors, diagnostics);
        var validation = new DocumentValidationResult(DocumentValidationDiagnostics.Normalize([.. diagnostics]));
        if (!validation.IsValid)
            return new(definition, plan: null, validation);

        var normalizedOperations = operations.Values;
        normalizedOperations.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        var normalizedActors = actors.Values;
        normalizedActors.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        actions.Sort(static (left, right) =>
        {
            var time = left.ScheduledAtUtc.CompareTo(right.ScheduledAtUtc);
            return time != 0 ? time : StringComparer.Ordinal.Compare(left.Id, right.Id);
        });

        var normalized = new ScenarioDefinition(
            definition.Id,
            definition.Revision,
            definition.InitialWorld,
            definition.StartsAtUtc,
            [.. normalizedOperations],
            [.. normalizedActors],
            [.. actions]);
        return new(
            definition,
            new(normalized, ScenarioCanonicalizer.ComputeDefinitionFingerprint(normalized)),
            validation);
    }

    static IdentitySet<ScenarioOperationDefinition> ValidateOperations(
        ScenarioDefinition definition,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        IdentitySet<ScenarioOperationDefinition> operations = new();
        if (definition.Operations.IsDefaultOrEmpty)
        {
            Add(
                diagnostics,
                code: "simulation.scenario.operationsMissing",
                message: "A scenario must declare at least one operation contract.",
                location: "/operations");
        }

        for (var index = 0; index < definition.Operations.Length; index++)
        {
            var operation = definition.Operations[index];
            var location = $"/operations/{index}";
            if (operation is null)
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.operationMissing",
                    message: "A scenario cannot contain a null operation contract.",
                    location);
                continue;
            }

            var added = operations.TryAdd(operation.Id, operation);
            if (!added)
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.operationIdentityDuplicate",
                    message: $"Operation identity '{operation.Id}' is declared more than once.",
                    location: $"{location}/id");
            }

            var inputIsPortable = ValidatePortableContract(
                operation.Input,
                $"{location}/input",
                diagnostics);
            var outputIsPortable = ValidatePortableContract(
                operation.Output,
                $"{location}/output",
                diagnostics);
            if (added && (!inputIsPortable || !outputIsPortable))
                operations.Remove(operation.Id);
        }

        return operations;
    }

    static IdentitySet<ScenarioActorDefinition> ValidateActors(
        ScenarioDefinition definition,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        IdentitySet<ScenarioActorDefinition> actors = new();
        if (definition.Actors.IsDefaultOrEmpty)
        {
            Add(
                diagnostics,
                code: "simulation.scenario.actorsMissing",
                message: "A scenario must bind at least one actor to the initial world.",
                location: "/actors");
        }

        for (var index = 0; index < definition.Actors.Length; index++)
        {
            var actor = definition.Actors[index];
            var location = $"/actors/{index}";
            if (actor is null)
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actorMissing",
                    message: "A scenario cannot contain a null actor.",
                    location);
                continue;
            }

            var added = actors.TryAdd(actor.Id, actor);
            if (!added)
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actorIdentityDuplicate",
                    message: $"Actor identity '{actor.Id}' is declared more than once.",
                    location: $"{location}/id");
            }

            var exemplarExists = true;
            if (!definition.InitialWorld.TryGetExemplar(actor.ExemplarId, out _))
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actorExemplarUnknown",
                    message: $"Actor '{actor.Id}' references unknown initial-world exemplar '{actor.ExemplarId}'.",
                    location: $"{location}/exemplarId");
                exemplarExists = false;
            }

            if (added && !exemplarExists)
                actors.Remove(actor.Id);
        }

        return actors;
    }

    static List<ScenarioActionDefinition> ValidateActions(
        ScenarioDefinition definition,
        IdentitySet<ScenarioOperationDefinition> operations,
        IdentitySet<ScenarioActorDefinition> actors,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        List<ScenarioActionDefinition> actions = [];
        HashSet<string> identities = new(StringComparer.Ordinal);
        if (definition.Actions.IsDefaultOrEmpty)
        {
            Add(
                diagnostics,
                code: "simulation.scenario.actionsMissing",
                message: "A scenario must schedule at least one action.",
                location: "/actions");
        }

        for (var index = 0; index < definition.Actions.Length; index++)
        {
            var action = definition.Actions[index];
            var location = $"/actions/{index}";
            if (action is null)
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actionMissing",
                    message: "A scenario cannot contain a null action.",
                    location);
                continue;
            }

            var usable = true;
            if (!identities.Add(action.Id))
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actionIdentityDuplicate",
                    message: $"Action identity '{action.Id}' is scheduled more than once.",
                    location: $"{location}/id");
                usable = false;
            }

            if (action.ScheduledAtUtc.Offset != TimeSpan.Zero)
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actionTimeNotUtc",
                    message: $"Action '{action.Id}' must use the UTC offset.",
                    location: $"{location}/scheduledAtUtc");
                usable = false;
            }
            else if (action.ScheduledAtUtc < definition.StartsAtUtc)
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actionBeforeStart",
                    message: $"Action '{action.Id}' is scheduled before the scenario virtual-time origin.",
                    location: $"{location}/scheduledAtUtc");
                usable = false;
            }

            if (!actors.Contains(action.ActorId))
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actionActorUnknown",
                    message: $"Action '{action.Id}' references unknown actor '{action.ActorId}'.",
                    location: $"{location}/actorId");
                usable = false;
            }

            if (action.TargetActorId is { } targetActorId && !actors.Contains(targetActorId))
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actionTargetUnknown",
                    message: $"Action '{action.Id}' references unknown target actor '{targetActorId}'.",
                    location: $"{location}/targetActorId");
                usable = false;
            }

            if (!operations.TryGet(action.OperationId, out var operation))
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actionOperationUnknown",
                    message: $"Action '{action.Id}' references unknown operation '{action.OperationId}'.",
                    location: $"{location}/operationId");
                usable = false;
            }
            else if (!operation.Input.IsSatisfiedByConstant(action.Input))
            {
                Add(
                    diagnostics,
                    code: "simulation.scenario.actionInputInvalid",
                    message: $"Action '{action.Id}' input does not satisfy operation '{operation.Id}'.",
                    location: $"{location}/input");
                usable = false;
            }

            if (usable)
                actions.Add(action);
        }

        return actions;
    }

    static bool ValidatePortableContract(
        ValueContract contract,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var validation = PortableExecutionValidator.Validate(contract);
        foreach (var diagnostic in validation.Diagnostics)
        {
            diagnostics.Add(diagnostic with
            {
                Location = PrefixLocation(location, diagnostic.Location),
                Evidence = new(stage: "scenario-compilation")
            });
        }

        return validation.IsValid;
    }

    static string PrefixLocation(string prefix, string? location) =>
        string.IsNullOrEmpty(location) || location == "/"
            ? prefix
            : location[0] == '/'
                ? prefix + location
                : $"{prefix}/{location}";

    static void Add(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string code,
        string message,
        string location) =>
        diagnostics.Add(new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location,
            Evidence: new(stage: "scenario-compilation")));

    sealed class IdentitySet<TValue>
        where TValue : class
    {
        readonly HashSet<string> identities = new(StringComparer.Ordinal);
        readonly Dictionary<string, TValue> values = new(StringComparer.Ordinal);

        public List<TValue> Values => [.. values.Values];

        public bool TryAdd(string id, TValue value)
        {
            if (!identities.Add(id))
                return false;

            values.Add(id, value);
            return true;
        }

        public bool Contains(string id) => values.ContainsKey(id);

        public bool TryGet(string id, out TValue value) => values.TryGetValue(id, out value!);

        public void Remove(string id) => values.Remove(id);
    }
}

static class ScenarioCanonicalizer
{
    public static string ComputeDefinitionFingerprint(ScenarioDefinition definition)
    {
        using SimulationFingerprintWriter writer = new();
        writer.Append(ScenarioDefinitionFingerprint.CurrentCanonicalization);
        writer.Append(definition.InitialWorld.ArtifactId.Value);
        writer.Append(definition.StartsAtUtc.ToString("O", CultureInfo.InvariantCulture));
        writer.Append(definition.Operations.Length);
        foreach (var operation in definition.Operations)
        {
            writer.Append(operation.Id);
            writer.Append(operation.Input);
            writer.Append(operation.Output);
        }

        writer.Append(definition.Actors.Length);
        foreach (var actor in definition.Actors)
        {
            writer.Append(actor.Id);
            writer.Append(actor.ExemplarId);
        }

        writer.Append(definition.Actions.Length);
        foreach (var action in definition.Actions)
        {
            writer.Append(action.Id);
            writer.Append(action.ScheduledAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.Append(action.ActorId);
            writer.Append(action.OperationId);
            writer.Append(action.TargetActorId ?? string.Empty);
            writer.Append(action.Input);
        }

        return writer.Complete();
    }
}
