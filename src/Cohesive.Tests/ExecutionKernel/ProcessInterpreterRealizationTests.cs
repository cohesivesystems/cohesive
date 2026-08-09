using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessInterpreterRealizationTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void ConstructCatalog_IsAnExactProjectionOfTheClosedPersistedNodeUnion()
    {
        string[] expected =
        [
            ProcessWireNames.InvokeTransitionNode,
            ProcessWireNames.EvaluateRelationNode,
            ProcessWireNames.RequestNode,
            ProcessWireNames.EmitEventNode,
            ProcessWireNames.SendSignalNode,
            ProcessWireNames.ChoiceNode,
            ProcessWireNames.MatchNode,
            ProcessWireNames.ForkNode,
            ProcessWireNames.JoinNode,
            ProcessWireNames.AwaitMatchNode,
            ProcessWireNames.TimerNode,
            ProcessWireNames.ReplyNode,
            ProcessWireNames.DurableCutNode,
            ProcessWireNames.InvokeProcessNode,
            ProcessWireNames.ForEachPartitionNode,
            ProcessWireNames.RepeatAcrossActivationNode,
            ProcessWireNames.ReturnNode,
            ProcessWireNames.FailNode
        ];
        var declaredDiscriminators = typeof(ProcessNode)
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(static attribute => Assert.IsType<string>(attribute.TypeDiscriminator))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), declaredDiscriminators);
        Assert.Equal(
            declaredDiscriminators,
            ProcessNodeConstructCatalog.DeclaredRequirements.Select(static requirement => requirement.Name));
        Assert.All(
            ProcessNodeConstructCatalog.DeclaredRequirements,
            static requirement => Assert.Equal(ProcessInterpreterRequirementCategory.Construct, requirement.Category));
    }

    [Fact]
    public void Collector_DerivesRequestConstructGuaranteesAndExactLinkedEvidence()
    {
        var plan = RequestPlan(out var requestReference);

        var inventory = ProcessInterpreterRequirementCollector.Collect(plan);

        Assert.Equal(plan.DefinitionReference, inventory.Definition);
        Assert.Equal(
            [ProcessWireNames.RequestNode, ProcessWireNames.ReturnNode],
            inventory.Requirements
                .Where(static requirement => requirement.Key.Category == ProcessInterpreterRequirementCategory.Construct)
                .Select(static requirement => requirement.Key.Name)
                .Order(StringComparer.Ordinal));
        var request = Assert.Single(
            inventory.Requirements,
            static requirement => requirement.Key
                == ProcessInterpreterRequirementKey.ForConstruct(ProcessWireNames.RequestNode));
        Assert.Equal(new ExecutionNodeId("request"), Assert.Single(request.Nodes));
        Assert.Equal(requestReference, Assert.Single(request.LinkedDefinitions));

        foreach (var guarantee in new[]
                 {
                     ProcessInterpreterGuarantees.InputAdmissionAndDisposition,
                     ProcessInterpreterGuarantees.DurableRequestRecovery,
                     ProcessInterpreterGuarantees.ExternalEffectDelivery
                 })
        {
            var requirement = Assert.Single(inventory.Requirements, candidate => candidate.Key == guarantee);
            Assert.Equal(new ExecutionNodeId("request"), Assert.Single(requirement.Nodes));
            Assert.Equal(requestReference, Assert.Single(requirement.LinkedDefinitions));
        }

        Assert.Contains(inventory.Requirements, static requirement =>
            requirement.Key == ProcessInterpreterGuarantees.ExactDefinitionPinning);
        Assert.DoesNotContain(inventory.Requirements, static requirement =>
            requirement.Key == ProcessInterpreterGuarantees.ForkJoinChildLineage);
        Assert.DoesNotContain(inventory.Requirements, static requirement =>
            requirement.Key == ProcessInterpreterGuarantees.WholeDefinitionAtomicity);
    }

    [Fact]
    public void Collector_AccountsForEveryDeclaredGuaranteeAcrossRepresentativePlans()
    {
        var plans = new[]
        {
            RequestPlan(out _),
            ForkPlan(),
            Compile(
                new CanonicalProcessDefinition(
                    StringContract,
                    StringContract,
                    new("return"),
                    [new ReturnProcessNode(new("return"), Expr.Const("done"))],
                    ProcessRecoveryPolicy.ContinueAttempt),
                contracts: null,
                new(ProcessAtomicScopeDemand.WholeDefinition))
        };

        var observed = plans
            .SelectMany(static plan => ProcessInterpreterRequirementCollector.Collect(plan).Requirements)
            .Where(static requirement => requirement.Key.Category == ProcessInterpreterRequirementCategory.Guarantee)
            .Select(static requirement => requirement.Key)
            .Distinct()
            .OrderBy(static requirement => requirement.Name, StringComparer.Ordinal);

        Assert.Equal(
            ProcessInterpreterGuarantees.All.OrderBy(static requirement => requirement.Name, StringComparer.Ordinal),
            observed);
    }

    [Fact]
    public void Compiler_ProducesExactlyOneDecisionForEveryInventoryRequirement()
    {
        var plan = ReturnPlan();
        var inventory = ProcessInterpreterRequirementCollector.Collect(plan);
        var profile = Profile(inventory, static requirement => Native(requirement.Key));

        var report = ProcessInterpreterRealizationCompiler.Compile(plan, profile);

        Assert.True(report.IsRealizable, Format(report.Diagnostics));
        Assert.Equal(ProcessInterpreterRealizationStatus.Realizable, report.Status);
        Assert.Equal(inventory.Requirements.Length, report.Decisions.Length);
        Assert.Empty(ProcessInterpreterRealizationLedger.ValidateCoverage(report.Inventory, report.Decisions));
        Assert.Equal(
            inventory.Requirements.Select(static requirement => requirement.Key),
            report.Decisions.Select(static decision => decision.Requirement));
        Assert.All(report.Decisions, static decision =>
            Assert.Equal(CapabilityRealizationKind.Native, decision.Realization));
    }

    [Fact]
    public void Compiler_EmitsUnavailableDecisionWhenProfileOmitsARequirement()
    {
        var plan = ReturnPlan();
        var inventory = ProcessInterpreterRequirementCollector.Collect(plan);
        var omitted = ProcessInterpreterRequirementKey.ForConstruct(ProcessWireNames.ReturnNode);
        var profile = Profile(
            inventory,
            requirement => requirement.Key == omitted ? null : Native(requirement.Key));

        var report = ProcessInterpreterRealizationCompiler.Compile(plan, profile);

        Assert.Equal(ProcessInterpreterRealizationStatus.NotRealizable, report.Status);
        Assert.Equal(
            CapabilityRealizationKind.Unavailable,
            Assert.Single(report.Decisions, decision => decision.Requirement == omitted).Realization);
        var diagnostic = Assert.Single(
            report.Diagnostics,
            candidate => candidate.Code == ProcessInterpreterRealizationDiagnosticCodes.RequirementMissing);
        Assert.Equal(omitted, diagnostic.Requirement);
        Assert.Equal(new ExecutionNodeId("return"), Assert.Single(diagnostic.Nodes));
        Assert.Empty(ProcessInterpreterRealizationLedger.ValidateCoverage(report.Inventory, report.Decisions));
    }

    [Fact]
    public void Compiler_DistinguishesUnavailableAmbiguousConstrainedAndInvalidEvidence()
    {
        var plan = ReturnPlan();
        var inventory = ProcessInterpreterRequirementCollector.Collect(plan);
        var selected = ProcessInterpreterRequirementKey.ForConstruct(ProcessWireNames.ReturnNode);

        var unavailable = ProcessInterpreterRealizationCompiler.Compile(
            plan,
            Profile(
                inventory,
                requirement => requirement.Key == selected
                    ? Evidence(requirement.Key, CapabilityRealizationKind.Unavailable, "unavailable")
                    : Native(requirement.Key)));
        Assert.Equal(ProcessInterpreterRealizationStatus.NotRealizable, unavailable.Status);
        Assert.Contains(unavailable.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessInterpreterRealizationDiagnosticCodes.RequirementUnavailable);

        var constrained = ProcessInterpreterRealizationCompiler.Compile(
            plan,
            Profile(
                inventory,
                requirement => requirement.Key == selected
                    ? Evidence(
                        requirement.Key,
                        CapabilityRealizationKind.Constrained,
                        "constrained",
                        boundaries: [new("limits/history-v1")])
                    : Native(requirement.Key)));
        Assert.True(constrained.IsRealizable, Format(constrained.Diagnostics));
        var constrainedDiagnostic = Assert.Single(
            constrained.Diagnostics,
            static diagnostic => diagnostic.Code
                == ProcessInterpreterRealizationDiagnosticCodes.RequirementConstrained);
        Assert.Equal(
            new ProcessInterpreterOperatingBoundaryId("limits/history-v1"),
            Assert.Single(constrainedDiagnostic.OperatingBoundaries));

        var ambiguousEvidence = Evidence(selected, CapabilityRealizationKind.Native, "ambiguous-a");
        var ambiguous = ProcessInterpreterRealizationCompiler.Compile(
            plan,
            Profile(
                inventory,
                requirement => requirement.Key == selected ? ambiguousEvidence : Native(requirement.Key),
                Evidence(selected, CapabilityRealizationKind.Native, "ambiguous-b")));
        Assert.Equal(ProcessInterpreterRealizationStatus.Invalid, ambiguous.Status);
        Assert.Contains(ambiguous.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessInterpreterRealizationDiagnosticCodes.StrategyAmbiguous);

        var invalid = ProcessInterpreterRealizationCompiler.Compile(
            plan,
            Profile(
                inventory,
                requirement => requirement.Key == selected
                    ? Evidence(requirement.Key, CapabilityRealizationKind.Composed, "invalid-composed")
                    : Native(requirement.Key)));
        Assert.Equal(ProcessInterpreterRealizationStatus.Invalid, invalid.Status);
        Assert.Contains(invalid.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessInterpreterRealizationDiagnosticCodes.CapabilityEvidenceInvalid);
    }

    [Fact]
    public void LedgerCoverageRejectsMissingDuplicatedAndUninventoriedDecisions()
    {
        var plan = ReturnPlan();
        var report = ProcessInterpreterRealizationCompiler.Compile(
            plan,
            Profile(
                ProcessInterpreterRequirementCollector.Collect(plan),
                static requirement => Native(requirement.Key)));
        Assert.True(report.IsRealizable, Format(report.Diagnostics));

        var missing = report.Decisions[0];
        var duplicated = report.Decisions[1];
        var extra = new ProcessInterpreterRealizationDecision(
            ProcessInterpreterRequirementKey.ForConstruct("futureNode"),
            CapabilityRealizationKind.Native,
            new("evidence/future-node"));
        var candidate = report.Decisions
            .Where(decision => decision.Requirement != missing.Requirement)
            .Append(duplicated)
            .Append(extra)
            .ToImmutableArray();

        var diagnostics = ProcessInterpreterRealizationLedger.ValidateCoverage(report.Inventory, candidate);

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, static diagnostic =>
            Assert.Equal(ProcessInterpreterRealizationDiagnosticCodes.LedgerCoverageMismatch, diagnostic.Code));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Requirement == missing.Requirement);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Requirement == duplicated.Requirement);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Requirement == extra.Requirement);
    }

    [Fact]
    public void TargetNeutralContracts_DoNotReferenceDurableTaskAssemblies()
    {
        Assert.DoesNotContain(
            typeof(ProcessInterpreterCapabilityProfile).Assembly.GetReferencedAssemblies(),
            static assembly => assembly.Name?.Contains("DurableTask", StringComparison.OrdinalIgnoreCase) == true);
    }

    static CompiledProcessPlan ReturnPlan() => Compile(
        new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            new("return"),
            [new ReturnProcessNode(new("return"), Expr.Const("done"))],
            ProcessRecoveryPolicy.ContinueAttempt),
        contracts: null);

    static CompiledProcessPlan RequestPlan(out ExecutionDefinitionReference requestReference)
    {
        var requestDocument = RequestDocument();
        requestReference = Reference(requestDocument);
        return Compile(
            new CanonicalProcessDefinition(
                StringContract,
                StringContract,
                new("request"),
                [
                    new RequestProcessNode(
                        new("request"),
                        new(requestReference),
                        Expr.BoundValue(ProcessBindingIds.Input),
                        [
                            new(
                                new("request/accepted"),
                                new("accepted"),
                                new(Edge("edge/request-return", "return")))
                        ]),
                    new ReturnProcessNode(new("return"), Expr.Const("done"))
                ],
                ProcessRecoveryPolicy.ContinueAttempt),
            Catalog(requestDocument));
    }

    static CompiledProcessPlan ForkPlan() => Compile(
        new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            new("fork"),
            [
                new ForkProcessNode(
                    new("fork"),
                    [new(new("fork/branch"), Edge("edge/fork-branch-join", "join"))],
                    new("join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    new(
                        ProcessJoinMode.All,
                        requiredCount: 0,
                        ProcessJoinFailurePolicy.FailFast,
                        ProcessJoinCancellationPolicy.AwaitRemaining,
                        ProcessJoinCompletionOrder.Unobservable,
                        ProcessJoinTieBreak.BranchIdentity),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ],
            ProcessRecoveryPolicy.ContinueAttempt),
        contracts: null);

    static CompiledProcessPlan Compile(
        CanonicalProcessDefinition definition,
        InteractionContractCatalog? contracts,
        ProcessCompilationOptions? options = null)
    {
        var document = ProcessDefinitionDocuments.Create(
            new("process/interpreter-realization-tests"),
            new("revision/1"),
            definition,
            Provenance());
        var result = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(interactionContracts: contracts),
            options ?? ProcessCompilationOptions.Default);
        Assert.True(result.IsSuccessful, Format(result.Validation));
        return Assert.IsType<CompiledProcessPlan>(result.Plan);
    }

    static ProcessInterpreterCapabilityProfile Profile(
        ProcessInterpreterRequirementInventory inventory,
        Func<ProcessInterpreterRequirement, ProcessInterpreterCapabilityEvidence?> create,
        params ReadOnlySpan<ProcessInterpreterCapabilityEvidence> additional)
    {
        var evidence = inventory.Requirements
            .Select(create)
            .Where(static candidate => candidate is not null)
            .Cast<ProcessInterpreterCapabilityEvidence>()
            .Concat(additional.ToArray())
            .ToImmutableArray();
        return new(
            new("tests/process-interpreter-profile/v1"),
            new("tests/process-interpreter"),
            evidence);
    }

    static ProcessInterpreterCapabilityEvidence Native(ProcessInterpreterRequirementKey requirement) =>
        Evidence(requirement, CapabilityRealizationKind.Native, "native");

    static ProcessInterpreterCapabilityEvidence Evidence(
        ProcessInterpreterRequirementKey requirement,
        CapabilityRealizationKind realization,
        string suffix,
        ImmutableArray<ProcessInterpreterCapabilityEvidenceId> auxiliaries = default,
        ImmutableArray<ProcessInterpreterOperatingBoundaryId> boundaries = default) => new(
            new($"evidence/{requirement.Category}/{requirement.Name}/{suffix}"),
            requirement,
            realization,
            auxiliaries,
            boundaries);

    static ExecutionDefinitionDocument RequestDocument() => InteractionContractDocuments.Create(
        new("interaction/request/interpreter-realization-tests"),
        new("revision/1"),
        new RequestContractDefinition(
            new(StringContract, new("request/v1")),
            new RequestResponseObligation(
                [
                    new RequestResultDefinition(
                        new("accepted"),
                        new(StringContract, new("accepted/v1")))
                ],
                RequestOptionalTerminalSemantics.Unsupported,
                RequestOptionalTerminalSemantics.Unsupported,
                RequestResultDisposition.Observe,
                RequestResultDisposition.Reject,
                RequestResultDisposition.ReusePriorDisposition,
                RequestRetrySemantics.StableIdentity,
                RequestResolutionSemantics.Reconcile,
                RequestResolutionSemantics.Escalate,
                TimeSpan.FromDays(30))),
        Provenance());

    static InteractionContractCatalog Catalog(params ExecutionDefinitionDocument[] documents)
    {
        var validation = InteractionContractCatalog.TryCreate(documents, out var catalog);
        Assert.True(validation.IsValid, Format(validation));
        return Assert.IsType<InteractionContractCatalog>(catalog);
    }

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static ExecutionProvenance Provenance() => new(
        new("process-interpreter-realization-tests", "1"),
        new("tests/execution-kernel/process-interpreter-realization"),
        DocumentOrigin.Generated);

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static string Format(IEnumerable<ProcessInterpreterRealizationDiagnostic> diagnostics) => string.Join(
        Environment.NewLine,
        diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
