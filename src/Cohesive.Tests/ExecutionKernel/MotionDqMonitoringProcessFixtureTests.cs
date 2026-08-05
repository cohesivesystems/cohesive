using System.Reflection;
using System.Text;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.MotionDq;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class MotionDqMonitoringProcessFixtureTests
{
    [Fact]
    public void IndependentAuthoring_IsDeterministicAndStrictlyRoundTrips()
    {
        var first = MotionDqMonitoringProcess.AuthorVersion1();
        var second = MotionDqMonitoringProcess.AuthorVersion1();

        Assert.NotSame(first, second);
        Assert.Equal(
            "4d762af6bfac07a63396526d8a9d9fd1fd60623f7ba91fd06a6398abae7cbe46",
            first.Document.Metadata.Fingerprint.Value);
        Assert.Equal(first.Definition, second.Definition);
        Assert.Equal(first.Reference, second.Reference);
        Assert.Equal(first.Document.Metadata.SourceMap, second.Document.Metadata.SourceMap);

        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(first.Document);
        var validation = ProcessDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.Equal(first.Document, restoredDocument);
        Assert.Equal(first.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));

        var compilation = ProcessStaticCompiler.Compile(restoredDocument!, first.LinkingContext);
        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        Assert.Equal(first.Definition, Assert.IsType<CompiledProcessPlan>(compilation.Plan).Definition);
    }

    [Fact]
    public void InputAndProgress_RetainReferencesInsteadOfAuthoritativeBusinessState()
    {
        var fixture = MotionDqMonitoringProcess.Version1;
        var input = Assert.IsType<ObjectTypeRef>(fixture.Definition.Input.Type);
        var recurrence = Node<RepeatAcrossActivationProcessNode>(
            fixture,
            "motion-dq/monitoring/repeat-across-activation");

        Assert.Collection(
            input.Fields,
            field =>
            {
                Assert.Equal(nameof(MotionDqMonitoringCaseReference.CaseId), field.Name);
                Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(field.Type).Kind);
            });
        Assert.Equal(365, recurrence.Policy.MaximumOccurrences);
        Assert.Equal(2, recurrence.Policy.MaximumUnchangedProgressOccurrences);
        Assert.Equal(ScalarTypeKind.Int64, Assert.IsType<ScalarTypeRef>(recurrence.ProgressContract.Type).Kind);
        Assert.DoesNotContain(
            ReachableContractTypes(typeof(MotionDqMonitoringCaseReference)),
            static type => type == typeof(MotionDqMonitoringObservation)
                || type == typeof(MotionDqInterventionWorkRequest)
                || type == typeof(MotionDqInterventionCompleted));
    }

    [Fact]
    public void Graph_UsesOneExactQueryAndOneDurableRecurrenceAsItsOnlyBackEdge()
    {
        var fixture = MotionDqMonitoringProcess.Version1;
        var query = Node<EvaluateRelationProcessNode>(
            fixture,
            "motion-dq/monitoring/evaluate-observation");
        var recurrence = Node<RepeatAcrossActivationProcessNode>(
            fixture,
            "motion-dq/monitoring/repeat-across-activation");

        Assert.Equal(fixture.ObservationQuery.Definition, query.Relation);
        Assert.Equal(ProcessDefinitionLinkKind.RelationQuery, fixture.ObservationQuery.Kind);
        Assert.Equal(fixture.Definition.Input, fixture.ObservationQuery.Input);
        Assert.Equal(recurrence.Id, Node<MatchProcessNode>(
            fixture,
            "motion-dq/monitoring/classify-observation").Cases
            .Single(static branch => branch.Id.Value == "motion-dq/monitoring/observation/cleared")
            .Next.Target);
        Assert.Equal(query.Id, recurrence.Repeat.Target);

        var edgesWithoutRecurrence = fixture.Definition.Nodes
            .SelectMany(Edges)
            .Where(edge => edge != (recurrence.Id, query.Id))
            .ToArray();
        AssertAcyclic(edgesWithoutRecurrence);
        Assert.DoesNotContain(
            fixture.Definition.Nodes,
            static node => node is DurableCutProcessNode);
    }

    [Fact]
    public void HumanWorkAwait_DeclaresCompletionCancellationSupersessionTimerAndLateEvidencePolicy()
    {
        var fixture = MotionDqMonitoringProcess.Version1;
        var request = Node<RequestProcessNode>(
            fixture,
            "motion-dq/monitoring/schedule-intervention");
        var wait = Node<AwaitMatchProcessNode>(
            fixture,
            "motion-dq/monitoring/await-intervention");

        Assert.Equal(fixture.Interactions.ScheduleInterventionRequest, request.Contract);
        Assert.Equal(ProcessAwaitArbitration.ExclusivePriorityThenClauseId, wait.Arbitration);
        Assert.Equal(ProcessAwaitInputDisposition.Observe, wait.LateInput);
        Assert.Equal(ProcessAwaitInputDisposition.Reject, wait.StaleInput);
        Assert.Equal(ProcessAwaitInputDisposition.ReusePriorDisposition, wait.DuplicateInput);
        Assert.Equal(ProcessAwaitMissingTargetDisposition.DeadLetter, wait.MissingTarget);
        Assert.Equal(TimeSpan.FromDays(90), wait.RetentionHorizon);

        var interactions = wait.Clauses.OfType<ProcessAwaitInteractionClause>().ToArray();
        var timer = Assert.Single(wait.Clauses.OfType<ProcessAwaitTimerClause>());
        Assert.Equal(3, interactions.Length);
        Assert.Contains(interactions, clause => clause.Contract == fixture.Interactions.InterventionCompletedSignal);
        Assert.Contains(interactions, clause => clause.Contract == fixture.Interactions.CaseCancellationSignal);
        Assert.Contains(interactions, clause => clause.Contract == fixture.Interactions.CaseSupersessionSignal);
        Assert.All(interactions, static clause => Assert.NotNull(clause.Guard));
        Assert.Equal("motion-dq/monitoring/intervention/evaluation-due", timer.Id.Value);
        Assert.Equal(
            "motion-dq/monitoring/repeat-across-activation",
            timer.Continuation.Edge.Target.Value);
    }

    [Fact]
    public void InteractionContracts_KeepExternalWorkAndEvidenceAsReferences()
    {
        var fixture = MotionDqMonitoringProcess.Version1;
        Assert.True(fixture.Interactions.Catalog.TryResolve(
            fixture.Interactions.ScheduleInterventionRequest,
            out var resolved));
        var request = Assert.IsType<RequestContractDefinition>(resolved);
        var scheduled = Assert.Single(
            request.Response.TerminalOutcomes,
            outcome => outcome.Id == MotionDqMonitoringInteractionContracts.InterventionScheduledOutcome);
        var result = Assert.IsType<ObjectTypeRef>(scheduled.Schema.Contract.Type);

        Assert.Collection(
            result.Fields,
            field =>
            {
                Assert.Equal(nameof(MotionDqInterventionWorkReference.WorkItemId), field.Name);
                Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(field.Type).Kind);
            });
        Assert.Single(
            fixture.Documents,
            document => document.Metadata.DefinitionId.Value == "process/motion-dq/monitoring");
        Assert.Single(fixture.ScheduleInterventionBinding.Replies,
            reply => reply.Outcome == MotionDqMonitoringInteractionContracts.InterventionScheduledOutcome);
    }

    [Fact]
    public void InterventionKind_IsTheClosedMotionDqMonitoringVocabulary()
    {
        Assert.Equal(
            [
                MotionDqInterventionKind.Coaching,
                MotionDqInterventionKind.VerbalWarning,
                MotionDqInterventionKind.Monitoring,
                MotionDqInterventionKind.Probation,
                MotionDqInterventionKind.Training,
                MotionDqInterventionKind.PostTrainingInspection,
                MotionDqInterventionKind.RoadTest,
                MotionDqInterventionKind.RideAlong
            ],
            Enum.GetValues<MotionDqInterventionKind>());
    }

    static TNode Node<TNode>(MotionDqMonitoringProcess fixture, string id)
        where TNode : ProcessNode =>
        Assert.IsType<TNode>(Assert.Single(fixture.Definition.Nodes, node => node.Id.Value == id));

    static IEnumerable<(ExecutionNodeId Source, ExecutionNodeId Target)> Edges(ProcessNode node)
    {
        IEnumerable<ProcessEdge> edges = node switch
        {
            EvaluateRelationProcessNode relation => [relation.Continuation.Edge],
            RequestProcessNode request => request.Outcomes.Select(static branch => branch.Continuation.Edge),
            MatchProcessNode match =>
                match.Cases.Select(static branch => branch.Next)
                    .Concat(match.Fallback is null ? [] : [match.Fallback.Next]),
            AwaitMatchProcessNode wait => wait.Clauses.Select(static clause => clause.Continuation.Edge),
            RepeatAcrossActivationProcessNode recurrence =>
                [recurrence.Repeat, recurrence.Completed, recurrence.Exhausted, recurrence.Stalled],
            _ => []
        };
        return edges.Select(edge => (node.Id, edge.Target));
    }

    static void AssertAcyclic(IEnumerable<(ExecutionNodeId Source, ExecutionNodeId Target)> edges)
    {
        var adjacency = edges
            .GroupBy(static edge => edge.Source)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static edge => edge.Target).ToArray());
        HashSet<ExecutionNodeId> visited = [];
        HashSet<ExecutionNodeId> visiting = [];

        bool Visit(ExecutionNodeId node)
        {
            if (visited.Contains(node))
            {
                return true;
            }

            if (!visiting.Add(node))
            {
                return false;
            }

            if (adjacency.TryGetValue(node, out var targets)
                && targets.Any(target => !Visit(target)))
            {
                return false;
            }

            visiting.Remove(node);
            visited.Add(node);
            return true;
        }

        Assert.All(adjacency.Keys, node => Assert.True(Visit(node), $"Free Process cycle reaches '{node.Value}'."));
    }

    static HashSet<Type> ReachableContractTypes(Type root)
    {
        HashSet<Type> reached = [];
        Queue<Type> pending = new([root]);
        while (pending.TryDequeue(out var current))
        {
            if (!reached.Add(current))
            {
                continue;
            }

            foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (type != typeof(string) && !type.IsPrimitive && !type.IsEnum && type != typeof(DateTimeOffset))
                {
                    pending.Enqueue(type);
                }
            }
        }

        return reached;
    }

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
