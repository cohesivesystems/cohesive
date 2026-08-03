using System.Reflection;
using System.Text;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.MotionDq;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class MotionDqCanonicalProcessFixtureTests
{
    [Fact]
    public void IndependentAuthoring_IsDeterministicAndStrictlyRoundTripsWithoutAuthoringState()
    {
        var first = MotionDqProcess.AuthorVersion1();
        var second = MotionDqProcess.AuthorVersion1();

        Assert.NotSame(first, second);
        Assert.NotSame(first.Authored, second.Authored);
        Assert.Equal(first.Definition, second.Definition);
        Assert.Equal(first.Reference, second.Reference);
        Assert.Equal(first.Document.Metadata.SourceMap, second.Document.Metadata.SourceMap);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(first.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(second.Document));

        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(first.Document);
        var validation = ProcessDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.NotNull(restoredDocument);
        Assert.NotNull(restoredDefinition);
        Assert.Equal(first.Document, restoredDocument);
        Assert.Equal(first.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument));

        var compilation = ProcessStaticCompiler.Compile(restoredDocument, first.LinkingContext);

        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        Assert.Equal(first.Definition, Assert.IsType<CompiledProcessPlan>(compilation.Plan).Definition);
    }

    [Fact]
    public void Input_IsBoundedCommandsAndReferences_NotResolvedProfileOrBusinessStateSnapshots()
    {
        var fixture = MotionDqProcess.Version1;
        var input = Assert.IsType<ObjectTypeRef>(fixture.Definition.Input.Type);

        Assert.Equal(
            [
                nameof(MotionDqOnboardingInput.ActivationAdmission),
                nameof(MotionDqOnboardingInput.Activations),
                nameof(MotionDqOnboardingInput.FullApplication),
                nameof(MotionDqOnboardingInput.InsuranceTerms),
                nameof(MotionDqOnboardingInput.InsuranceTermsAdmission),
                nameof(MotionDqOnboardingInput.PostTerms),
                nameof(MotionDqOnboardingInput.PostTermsAdmission),
                nameof(MotionDqOnboardingInput.Prequalification),
                nameof(MotionDqOnboardingInput.ReviewDueAtUtc),
                nameof(MotionDqOnboardingInput.ReviewTask),
                nameof(MotionDqOnboardingInput.ReviewTimeoutCancellation)
            ],
            input.Fields.Select(static field => field.Name).Order(StringComparer.Ordinal));
        AssertNoCollectionContracts(input);
        Assert.DoesNotContain(
            FlattenFields(input),
            static field => field.Name is "Milestone" or "Status" or "Evaluations" or "EvidenceHistory"
                or "Blocks" or "Requirements" or "EvidenceNeeds" or "Gates" or "SubjectSlots");

        var reachableTypes = ReachableContractTypes(typeof(MotionDqOnboardingInput));
        Assert.DoesNotContain(typeof(MotionDqCaseProfileResolution), reachableTypes);
        Assert.DoesNotContain(typeof(MotionDqResolvedProfile), reachableTypes);
        Assert.DoesNotContain(
            reachableTypes,
            static type => type != typeof(string)
                && type.GetInterfaces().Any(static candidate => candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)));
    }

    [Fact]
    public void ReviewAwait_DeclaresAllWinnersPoliciesAndDurableHoldCycle()
    {
        var fixture = MotionDqProcess.Version1;
        var wait = Node<AwaitMatchProcessNode>(fixture, "motion-dq/review/await-match");

        Assert.Equal(ProcessAwaitArbitration.ExclusivePriorityThenClauseId, wait.Arbitration);
        Assert.Equal(ProcessAwaitInputDisposition.Observe, wait.LateInput);
        Assert.Equal(ProcessAwaitInputDisposition.Reject, wait.StaleInput);
        Assert.Equal(ProcessAwaitInputDisposition.ReusePriorDisposition, wait.DuplicateInput);
        Assert.Equal(ProcessAwaitMissingTargetDisposition.DeadLetter, wait.MissingTarget);
        Assert.Equal(TimeSpan.FromDays(30), wait.RetentionHorizon);

        var interactions = wait.Clauses.OfType<ProcessAwaitInteractionClause>().ToArray();
        var timer = Assert.Single(wait.Clauses.OfType<ProcessAwaitTimerClause>());
        Assert.Equal(4, interactions.Length);
        Assert.Equal("motion-dq/review/timed-out", timer.Id.Value);
        Assert.Equal(
            ["motion-dq/review/cancelled", "motion-dq/review/hire", "motion-dq/review/hold", "motion-dq/review/not-eligible"],
            interactions.Select(static clause => clause.Id.Value).Order(StringComparer.Ordinal));
        Assert.Equal(
            3,
            interactions.Count(clause => clause.Contract == fixture.Interactions.ReviewDecisionSignal));
        Assert.Single(
            interactions,
            clause => clause.Contract == fixture.Interactions.CaseCancellationSignal);
        Assert.All(interactions, static clause => Assert.NotNull(clause.Guard));
        Assert.All(
            interactions.Where(clause => clause.Contract == fixture.Interactions.ReviewDecisionSignal),
            clause =>
            {
                var paths = FieldPaths(Assert.IsAssignableFrom<Expr>(clause.Guard)).ToArray();
                Assert.Contains(paths, static path => path.EndsWith("CaseId", StringComparison.Ordinal));
                Assert.Contains(paths, static path => path.EndsWith("ApplicationId", StringComparison.Ordinal));
                Assert.Contains(paths, static path => path.EndsWith("Kind", StringComparison.Ordinal));
            });
        var cancellationClause = Assert.Single(
            interactions,
            clause => clause.Contract == fixture.Interactions.CaseCancellationSignal);
        Assert.Contains("CaseId", FieldPaths(Assert.IsAssignableFrom<Expr>(cancellationClause.Guard)));

        var hold = Assert.Single(interactions, static clause => clause.Id.Value == "motion-dq/review/hold");
        Assert.Equal("motion-dq/review/record-hold", hold.Continuation.Edge.Target.Value);
        var recordHold = Node<InvokeTransitionProcessNode>(fixture, "motion-dq/review/record-hold");
        var requiredOutcome = Node<MatchProcessNode>(
            fixture,
            "motion-dq/review/record-hold/require-outcome");
        Assert.Equal(requiredOutcome.Id, recordHold.Continuation.Edge.Target);
        Assert.Equal(
            wait.Id,
            Assert.Single(requiredOutcome.Cases).Next.Target);
        Assert.Contains(
            fixture.Plan.EffectSummary.Effects,
            effect => effect.Node == wait.Id && effect.Kind == ProcessEffectKind.DurableWait);
    }

    [Fact]
    public void ReviewTask_LeavesLifecycleExternalAndRetainsOnlyTypedTaskReference()
    {
        var fixture = MotionDqProcess.Version1;
        var request = Node<RequestProcessNode>(fixture, "motion-dq/review/create-task");
        var created = Assert.Single(
            request.Outcomes,
            branch => branch.Outcome == MotionDqInteractionContracts.ReviewTaskCreatedOutcome);
        var output = Assert.IsType<ProcessOutputBinding>(created.Continuation.Output);
        var taskReference = Assert.IsType<ObjectTypeRef>(output.Contract.Type);

        Assert.Equal(fixture.Interactions.ReviewTaskRequest, request.Contract);
        Assert.Equal("motion-dq/review/await-match", created.Continuation.Edge.Target.Value);
        Assert.Collection(
            taskReference.Fields,
            field =>
            {
                Assert.Equal(nameof(MotionDqReviewTaskReference.TaskId), field.Name);
                Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(field.Type).Kind);
            });
    }

    [Fact]
    public void PostTerms_UsesOneExactRequestAcrossVendorAndManualRoutesThenJoinsSevenBranches()
    {
        var fixture = MotionDqProcess.Version1;
        var fulfillmentRequests = fixture.Definition.Nodes
            .OfType<RequestProcessNode>()
            .Where(node => node.Contract == fixture.Interactions.FulfillRequirementRequest)
            .ToArray();
        var vendorRequests = fulfillmentRequests
            .Where(static node => node.Id.Value.EndsWith("/vendor", StringComparison.Ordinal))
            .ToArray();
        var manualRequests = fulfillmentRequests
            .Where(static node => node.Id.Value.EndsWith("/manual", StringComparison.Ordinal))
            .ToDictionary(static node => node.Id.Value, StringComparer.Ordinal);

        Assert.Equal(14, fulfillmentRequests.Length);
        Assert.Equal(7, vendorRequests.Length);
        Assert.Equal(7, manualRequests.Count);
        Assert.All(
            fulfillmentRequests,
            node => Assert.Equal(fixture.Interactions.FulfillRequirementRequest, node.Contract));
        Assert.Single(
            fixture.RequestBindings,
            binding => binding.Request == fixture.Interactions.FulfillRequirementRequest);

        foreach (var vendor in vendorRequests)
        {
            var manualId = vendor.Id.Value[..^"vendor".Length] + "manual";
            var manual = manualRequests[manualId];
            Assert.Equal(vendor.Contract, manual.Contract);
            Assert.Equal(vendor.Payload, manual.Payload);
            Assert.Equal(
                manual.Id,
                Assert.Single(
                    vendor.Outcomes,
                    branch => branch.Outcome == MotionDqInteractionContracts.RequirementProviderFailedOutcome)
                    .Continuation.Edge.Target);
            Assert.Equal(
                manual.Id,
                Assert.Single(
                    vendor.Outcomes,
                    branch => branch.Outcome == MotionDqInteractionContracts.RequirementProviderTimedOutOutcome)
                    .Continuation.Edge.Target);
            Assert.Equal(
                manual.Id,
                Assert.Single(
                    vendor.Outcomes,
                    branch => branch.Outcome == MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome)
                    .Continuation.Edge.Target);
            Assert.All(
                manual.Outcomes.Where(branch => branch.Outcome != MotionDqInteractionContracts.RequirementFulfilledOutcome),
                branch =>
                {
                    Assert.Equal("motion-dq/post-terms/join", branch.Continuation.Edge.Target.Value);
                    Assert.Null(branch.Continuation.Output);
                });
        }

        var fork = Node<ForkProcessNode>(fixture, "motion-dq/post-terms/fork");
        var join = Node<JoinProcessNode>(fixture, "motion-dq/post-terms/join");
        Assert.Equal(7, fork.Branches.Length);
        Assert.Equal(join.Id, fork.Join);
        Assert.Equal(fork.Id, join.Fork);
        Assert.Equal(ProcessJoinMode.All, join.Policy.Mode);
        Assert.Equal(ProcessJoinFailurePolicy.FailFast, join.Policy.Failure);
        Assert.Equal(ProcessJoinCancellationPolicy.AwaitRemaining, join.Policy.Cancellation);
        Assert.Equal(ProcessJoinCompletionOrder.Unobservable, join.Policy.CompletionOrder);
        Assert.Equal(ProcessJoinTieBreak.BranchIdentity, join.Policy.TieBreak);
        Assert.All(
            fork.Branches,
            branch => Assert.EndsWith("/vendor", branch.Start.Target.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void FulfillmentNonSuccess_IsTypedAttemptEvidenceAndCannotEnterRequirementTransition()
    {
        var fixture = MotionDqProcess.Version1;
        Assert.True(
            fixture.InteractionCatalog.TryResolve(
                fixture.Interactions.FulfillRequirementRequest,
                out var resolved));
        var request = Assert.IsType<RequestContractDefinition>(resolved);
        var fulfilled = Assert.Single(
            request.Response.TerminalOutcomes,
            outcome => outcome.Id == MotionDqInteractionContracts.RequirementFulfilledOutcome);
        var failed = Assert.Single(
            request.Response.TerminalOutcomes,
            outcome => outcome.Id == MotionDqInteractionContracts.RequirementProviderFailedOutcome);
        var timedOut = Assert.Single(
            request.Response.TerminalOutcomes,
            outcome => outcome.Id == MotionDqInteractionContracts.RequirementProviderTimedOutOutcome);
        var cancelled = Assert.Single(
            request.Response.TerminalOutcomes,
            outcome => outcome.Id == MotionDqInteractionContracts.RequirementFulfillmentCancelledOutcome);

        Assert.NotEqual(fulfilled.Schema, failed.Schema);
        Assert.Equal(failed.Schema, timedOut.Schema);
        Assert.Equal(failed.Schema, cancelled.Schema);
        var failureType = Assert.IsType<ObjectTypeRef>(failed.Schema.Contract.Type);
        Assert.Contains(
            failureType.Fields,
            static field => field.Name == nameof(MotionDqRequirementFulfillmentFailure.ProviderAttemptId));
        Assert.DoesNotContain(
            failureType.Fields,
            static field => field.Name == nameof(MotionDqInsuranceTermsResult.Evaluation));

        var requirementTransition = fixture.Transitions.ApplyRequirementEvaluation.Reference;
        var applications = fixture.Definition.Nodes
            .OfType<InvokeTransitionProcessNode>()
            .Where(node => node.Transition == requirementTransition)
            .Where(static node => node.Id.Value.Contains("/post-terms/", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(14, applications.Length);
        Assert.All(
            applications,
            static node => Assert.True(
                node.Id.Value.EndsWith("/vendor-fulfilled", StringComparison.Ordinal)
                || node.Id.Value.EndsWith("/manual-fulfilled", StringComparison.Ordinal)));
    }

    [Fact]
    public void CoordinationCommands_ArePreflightedAndThreeMilestoneEdgesAreExplicit()
    {
        var fixture = MotionDqProcess.Version1;
        var postTermsPreflight = Node<ChoiceProcessNode>(fixture, "motion-dq/post-terms/validate-requests");
        var activationPreflight = Node<ChoiceProcessNode>(fixture, "motion-dq/activation/validate-input");
        Assert.Equal("motion-dq/post-terms/fork", Assert.Single(postTermsPreflight.Cases).Next.Target.Value);
        Assert.Equal("motion-dq/activation/carrier-owner-operator", Assert.Single(activationPreflight.Cases).Next.Target.Value);
        Assert.Equal(
            "motion-dq/terminal/coordination-rejected",
            Assert.IsType<ProcessFallback>(postTermsPreflight.Fallback).Next.Target.Value);
        Assert.Equal(
            "motion-dq/terminal/coordination-rejected",
            Assert.IsType<ProcessFallback>(activationPreflight.Fallback).Next.Target.Value);
        var postTermsPaths = FieldPaths(Assert.Single(postTermsPreflight.Cases).Predicate).ToArray();
        Assert.Equal(7, postTermsPaths.Count(static path => path.EndsWith(".EvidenceNeedId", StringComparison.Ordinal)));
        Assert.Equal(
            7,
            postTermsPaths.Count(static path => path.EndsWith(".Requirement.RequirementId", StringComparison.Ordinal)));
        Assert.Equal(
            7,
            postTermsPaths.Count(static path => path.EndsWith(".Requirement.CaseId", StringComparison.Ordinal)));
        var activationPaths = FieldPaths(Assert.Single(activationPreflight.Cases).Predicate).ToArray();
        Assert.Contains(
            activationPaths,
            static path => path.EndsWith(".Admission.ParentCarrierProof.CarrierSubject", StringComparison.Ordinal));
        Assert.Contains(
            activationPaths,
            static path => path.EndsWith(".Admission.ParentCarrierProof.ActivationDecisionId", StringComparison.Ordinal));
        Assert.Contains(
            activationPaths,
            static path => path.EndsWith(".Admission.ParentCarrierProof.EvidenceId", StringComparison.Ordinal));
        Assert.Contains(
            activationPaths,
            static path => path.EndsWith(".Subject.ParentApplicationId", StringComparison.Ordinal));

        var milestoneTransition = fixture.Transitions.AdvanceCaseMilestone.Reference;
        var milestones = fixture.Definition.Nodes
            .OfType<InvokeTransitionProcessNode>()
            .Where(node => node.Transition == milestoneTransition)
            .ToArray();
        Assert.Equal(
            [
                "motion-dq/case/advance-activation",
                "motion-dq/case/advance-insurance-terms",
                "motion-dq/case/advance-post-terms"
            ],
            milestones.Select(static node => node.Id.Value).Order(StringComparer.Ordinal));
        Assert.All(milestones, node => Assert.IsType<MatchProcessNode>(
            Assert.Single(
                fixture.Definition.Nodes,
                candidate => candidate.Id.Value == $"{node.Id.Value}/require-outcome")));
    }

    [Fact]
    public void Activation_AdmitsCarrierBeforeFourIndependentSubjectAuthorities()
    {
        var fixture = MotionDqProcess.Version1;
        var activationTransition = fixture.Transitions.ActivateSubject.Reference;
        var carrier = Node<InvokeTransitionProcessNode>(
            fixture,
            "motion-dq/activation/carrier-owner-operator");
        var fork = Node<ForkProcessNode>(fixture, "motion-dq/activation/independent/fork");
        var join = Node<JoinProcessNode>(fixture, "motion-dq/activation/independent/join");
        var activations = fixture.Definition.Nodes
            .OfType<InvokeTransitionProcessNode>()
            .Where(node => node.Transition == activationTransition)
            .ToArray();

        var carrierOutcome = Node<MatchProcessNode>(
            fixture,
            "motion-dq/activation/carrier-owner-operator/require-outcome");
        Assert.Equal(carrierOutcome.Id, carrier.Continuation.Edge.Target);
        Assert.Equal(fork.Id, Assert.Single(carrierOutcome.Cases).Next.Target);
        Assert.Equal(4, fork.Branches.Length);
        Assert.Equal(join.Id, fork.Join);
        Assert.Equal(fork.Id, join.Fork);
        Assert.Equal(
            ["applicant", "driver", "trailer", "truck"],
            fork.Branches.Select(static branch => branch.Start.Target.Value.Split('/')[^1]));
        Assert.Equal(5, activations.Length);
        Assert.Equal(5, activations.Select(static node => node.Subject).Distinct().Count());
        Assert.All(activations, static node =>
        {
            var subject = Assert.IsType<FieldExpr>(node.Subject);
            Assert.Equal(ProcessBindingIds.Input, subject.Binding);
            Assert.Equal("Subject", subject.Path.Segments[^1].Segment);
        });
    }

    [Fact]
    public void WholeDefinitionAtomicDemand_IsRejectedByDurableAndExternalEffects()
    {
        var fixture = MotionDqProcess.Version1;

        var compilation = ProcessStaticCompiler.Compile(
            fixture.Document,
            fixture.LinkingContext,
            new(ProcessAtomicScopeDemand.WholeDefinition));
        var replay = ProcessStaticCompiler.Compile(
            fixture.Document,
            fixture.LinkingContext,
            new(ProcessAtomicScopeDemand.WholeDefinition));

        Assert.False(compilation.IsSuccessful);
        Assert.Null(compilation.Plan);
        Assert.Equivalent(compilation.Validation, replay.Validation, strict: true);
        Assert.Contains(
            compilation.Validation.Diagnostics,
            static diagnostic => diagnostic.Code
                == ProcessCompilationDiagnosticCodes.AtomicScopeCrossesDurableBoundary);
        Assert.Contains(
            compilation.Validation.Diagnostics,
            static diagnostic => diagnostic.Code
                == ProcessCompilationDiagnosticCodes.AtomicScopeContainsExternalInteraction);
    }

    static TNode Node<TNode>(MotionDqProcess fixture, string id)
        where TNode : ProcessNode => Assert.IsType<TNode>(
            Assert.Single(fixture.Definition.Nodes, node => node.Id.Value == id));

    static IEnumerable<ObjectFieldTypeDef> FlattenFields(ObjectTypeRef root)
    {
        var pending = new Stack<ObjectTypeRef>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            foreach (var field in current.Fields)
            {
                yield return field;
                if (field.Type is ObjectTypeRef nested)
                    pending.Push(nested);
            }
        }
    }

    static void AssertNoCollectionContracts(ObjectTypeRef root)
    {
        foreach (var field in FlattenFields(root))
        {
            Assert.Equal(FieldCardinality.Single, field.Cardinality);
            Assert.IsNotType<ArrayTypeRef>(field.Type);
        }
    }

    static HashSet<Type> ReachableContractTypes(Type root)
    {
        HashSet<Type> visited = [];
        var pending = new Stack<Type>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            current = Nullable.GetUnderlyingType(current) ?? current;
            if (!visited.Add(current)
                || current.IsPrimitive
                || current.IsEnum
                || current == typeof(string)
                || current == typeof(DateTimeOffset))
            {
                continue;
            }

            foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                pending.Push(property.PropertyType);
        }

        return visited;
    }

    static IEnumerable<string> FieldPaths(Expr expression)
    {
        switch (expression)
        {
            case FieldExpr field:
                yield return string.Join('.', field.Path.Segments.Select(static segment => segment.Segment));
                break;
            case BinaryExpr binary:
                foreach (var path in FieldPaths(binary.Left))
                    yield return path;
                foreach (var path in FieldPaths(binary.Right))
                    yield return path;
                break;
        }
    }

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
