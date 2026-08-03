using System.Reflection;
using Cohesive.Tests.Storage;

namespace Cohesive.Tests.ExecutionKernel;

/// <summary>
/// Enforces the EK-01 through EK-11 closeout gate by indexing the existing executable conformance tests. The
/// normative scenarios remain owned by the Execution Kernel specification; this matrix contains only test
/// references and realization-profile evidence.
/// </summary>
public sealed class ExecutionKernelConformanceMatrixTests
{
    static readonly string[] RequiredScenarios =
    [
        "EK-01",
        "EK-02",
        "EK-03",
        "EK-04",
        "EK-05",
        "EK-06",
        "EK-07",
        "EK-08",
        "EK-09",
        "EK-10",
        "EK-11"
    ];

    static readonly ConformanceEntry[] Matrix =
    [
        Semantic(
            "EK-01",
            typeof(MotionDqCanonicalTransitionFixtureTests),
            nameof(MotionDqCanonicalTransitionFixtureTests.CaseTransitions_EnforceProfileApplicationReviewAndCancellationGates)),
        Semantic(
            "EK-01",
            typeof(MotionDqCanonicalTransitionFixtureTests),
            nameof(MotionDqCanonicalTransitionFixtureTests.RequirementTransition_ClassifiesReplayCollisionAndSupersededEvidenceWithoutReplacingAuthority)),
        Semantic(
            "EK-01",
            typeof(TransitionReferenceInterpreterTests),
            nameof(TransitionReferenceInterpreterTests.Decide_Ek01Branches_FullAndSparseProduceEquivalentDecisions)),

        Semantic(
            "EK-02",
            typeof(MotionDqDurableProcessConformanceTests),
            nameof(MotionDqDurableProcessConformanceTests.HoldCycle_RestoresFreshWait_ThenHigherPriorityHireBeatsDueTimer)),
        Semantic(
            "EK-02",
            typeof(MotionDqDurableProcessConformanceTests),
            nameof(MotionDqDurableProcessConformanceTests.ReviewTimeout_RestoresExactWait_AndConvergesWithoutLiveExecution)),

        Semantic(
            "EK-03",
            typeof(MotionDqDurableProcessConformanceTests),
            nameof(MotionDqDurableProcessConformanceTests.VendorFailure_DoesNotSettleRequirement_AndManualFallbackAppliesExactlyOnce)),

        Semantic(
            "EK-04",
            typeof(MotionDqDurableProcessConformanceTests),
            nameof(MotionDqDurableProcessConformanceTests.PostTermsCompletionOrder_IsSemanticallyUnobservableAndReplayStable)),

        Semantic(
            "EK-05",
            typeof(MotionDqCanonicalProcessFixtureTests),
            nameof(MotionDqCanonicalProcessFixtureTests.WholeDefinitionAtomicDemand_IsRejectedByDurableAndExternalEffects)),
        Semantic(
            "EK-05",
            typeof(MotionDqDurableProcessConformanceTests),
            nameof(MotionDqDurableProcessConformanceTests.ConcurrentSubjectActivation_PreservesIndependentAuthorityAndFailsDifferentially)),

        Semantic(
            "EK-06",
            typeof(InMemoryProcessDurableStoreCrashTests),
            nameof(InMemoryProcessDurableStoreCrashTests.AfterAtomicCommitBeforeReturn_ExposesAllAndExactRetryReplays)),
        Semantic(
            "EK-06",
            typeof(InMemoryProcessDurableStoreCrashTests),
            nameof(InMemoryProcessDurableStoreCrashTests.BeforeAtomicCommit_ExposesNoneAndExactRetryApplies)),
        Semantic(
            "EK-06",
            typeof(ProcessDurableRuntimeOperationTests),
            nameof(ProcessDurableRuntimeOperationTests.Ek06_CrashAfterDispatchCommitBeforeExternalCallRecoversExactRequest)),
        Semantic(
            "EK-06",
            typeof(ProcessDurableRuntimeOperationTests),
            nameof(ProcessDurableRuntimeOperationTests.Ek06_CrashBeforeAcknowledgementCommitRedispatchesStableAttemptAndDeduplicationKey)),
        Semantic(
            "EK-06",
            typeof(ProcessDurableRuntimeOperationTests),
            nameof(ProcessDurableRuntimeOperationTests.Ek06_CrashBeforeResultAdmissionLaterCommitsExactlyOneDeterministicReply)),

        Semantic(
            "EK-07",
            typeof(ProcessDurableRuntimeTests),
            nameof(ProcessDurableRuntimeTests.Ek07_SignalTimerWinnersAreOrderIndependentAcrossBufferingRestoreAndClosedWaitPolicies)),

        Semantic(
            "EK-08",
            typeof(MaterializationRebuildExecutorTests),
            nameof(MaterializationRebuildExecutorTests.CrashAtEveryPageBoundary_ResumesSameGenerationWithoutDuplicateEffects)),
        Semantic(
            "EK-08",
            typeof(MaterializationRebuildProcessConformanceTests),
            nameof(MaterializationRebuildProcessConformanceTests.CanonicalLeafCoordinator_DrivesBaselineCatchUpToReadyThenActivationConsumesExactEvidence)),
        AdapterQualification(
            "EK-08",
            typeof(IndexSyncVerticalSliceTests),
            nameof(IndexSyncVerticalSliceTests.Ek08_CosmosAndPostgresCapabilitiesDoNotChangeCanonicalProcessMeaning),
            capabilityEvidence:
                "Cosmos and PostgreSQL source capability profiles plus the Elasticsearch target capability profile"),
        AdapterQualification(
            "EK-08",
            typeof(IndexSyncVerticalSliceTests),
            nameof(IndexSyncVerticalSliceTests.SharedRelation_RebuildsResumesConvergesAndPromotesThroughRealAdapters),
            capabilityEvidence:
                "Cosmos and PostgreSQL source capability profiles plus the Elasticsearch target capability profile"),

        Semantic(
            "EK-09",
            typeof(CanonicalProcessAuthoringTests),
            nameof(CanonicalProcessAuthoringTests.AuthoredDocument_StrictRoundTripCompilesAndReferenceInterpretsWithoutProducerAssemblyState)),
        Semantic(
            "EK-09",
            typeof(CanonicalProcessAuthoringTests),
            nameof(CanonicalProcessAuthoringTests.TypedCSharpAuthoring_LowersToEquivalentDirectCanonicalIrDeterministically)),
        Semantic(
            "EK-09",
            typeof(CanonicalTransitionAuthoringTests),
            nameof(CanonicalTransitionAuthoringTests.AuthoredDocument_StrictRoundTripCompilesAndReferenceInterprets)),
        Semantic(
            "EK-09",
            typeof(CanonicalTransitionAuthoringTests),
            nameof(CanonicalTransitionAuthoringTests.TypedCSharpAuthoring_LowersToEquivalentDirectCanonicalIrDeterministically)),

        Semantic(
            "EK-10",
            typeof(InMemoryProcessDurableStoreCrashTests),
            nameof(InMemoryProcessDurableStoreCrashTests.AcquisitionExactRetry_ReplaysAcrossInterveningInboxRevision)),
        Semantic(
            "EK-10",
            typeof(InMemoryProcessDurableStoreCrashTests),
            nameof(InMemoryProcessDurableStoreCrashTests.AfterAtomicCommitBeforeReturn_ExposesAllAndExactRetryReplays)),
        Semantic(
            "EK-10",
            typeof(InMemoryProcessDurableStoreCrashTests),
            nameof(InMemoryProcessDurableStoreCrashTests.BeforeAtomicCommit_ExposesNoneAndExactRetryApplies)),
        Semantic(
            "EK-10",
            typeof(InMemoryProcessDurableStoreCrashTests),
            nameof(InMemoryProcessDurableStoreCrashTests.RenewalExactRetry_ReplaysAcrossLaterInterveningInboxChronology)),
        Semantic(
            "EK-10",
            typeof(InMemoryProcessDurableStoreCrashTests),
            nameof(InMemoryProcessDurableStoreCrashTests.TerminalCheckpoint_StillDurablyAdmitsLateInputForPolicyClassification)),
        Semantic(
            "EK-10",
            typeof(InMemoryProcessDurableStoreTests),
            nameof(InMemoryProcessDurableStoreTests.Commit_ExactReplayPrecedesRevisionAndFenceChecksWhileChangedIdentityConflicts)),
        Semantic(
            "EK-10",
            typeof(InMemoryProcessDurableStoreTests),
            nameof(InMemoryProcessDurableStoreTests.ConcurrentCommits_PublishOneCompleteWinnerWithoutMixingState)),
        Semantic(
            "EK-10",
            typeof(InMemoryProcessDurableStoreTests),
            nameof(InMemoryProcessDurableStoreTests.InputAdmissionAfterWorkerLoad_InvalidatesCommitWithoutLosingWakeup)),
        Semantic(
            "EK-10",
            typeof(InMemoryProcessDurableStoreTests),
            nameof(InMemoryProcessDurableStoreTests.WorkerLease_AcquireReplayHeldRenewAndReclaimAdvanceMonotonicFence)),

        Semantic(
            "EK-11",
            typeof(MotionDqMonitoringDurableConformanceTests),
            nameof(MotionDqMonitoringDurableConformanceTests.FullMonitoringTimeline_RestoresRecursAndCreatesEachHumanWorkItemOnce)),
        Semantic(
            "EK-11",
            typeof(ProcessHigherOrderReferenceInterpreterTests),
            nameof(ProcessHigherOrderReferenceInterpreterTests.RepeatAcrossActivation_BodyDurableCutRoundTripsWhileRecurrenceWaitIsATombstone)),
        Semantic(
            "EK-11",
            typeof(ProcessHigherOrderReferenceInterpreterTests),
            nameof(ProcessHigherOrderReferenceInterpreterTests.RepeatAcrossActivation_CheckpointRoundTripPreservesProgressAndRecoveredRuntimeResumesNextOccurrence)),
        Semantic(
            "EK-11",
            typeof(ProcessHigherOrderReferenceInterpreterTests),
            nameof(ProcessHigherOrderReferenceInterpreterTests.RepeatAcrossActivation_CompletesAfterOneDeterministicPollingOccurrencePerActivation)),
        Semantic(
            "EK-11",
            typeof(ProcessHigherOrderReferenceInterpreterTests),
            nameof(ProcessHigherOrderReferenceInterpreterTests.RepeatAcrossActivation_RoutesToExhaustedAfterTheFiniteOccurrenceBudget)),
        Semantic(
            "EK-11",
            typeof(ProcessHigherOrderReferenceInterpreterTests),
            nameof(ProcessHigherOrderReferenceInterpreterTests.RepeatAcrossActivation_RoutesToStalledWhenAuthoredProgressStopsChanging))
    ];

    [Fact]
    public void RequiredScenarios_HaveExecutableNonSkippedSemanticConformance()
    {
        Assert.Equal(
            RequiredScenarios,
            Matrix.Select(static entry => entry.Scenario).Distinct(StringComparer.Ordinal));

        foreach (var scenario in RequiredScenarios)
        {
            Assert.Contains(
                Matrix,
                entry => entry.Scenario == scenario && entry.Profile == ConformanceProfile.Semantic);
        }

        var duplicate = Matrix
            .GroupBy(
                static entry => (entry.Scenario, entry.Profile, entry.TestClass, entry.TestMethod),
                EqualityComparer<(string, ConformanceProfile, Type, string)>.Default)
            .FirstOrDefault(static group => group.Count() != 1);
        Assert.True(duplicate is null, $"Duplicate conformance reference: {duplicate?.Key}.");

        Assert.Equal(
            Matrix.Select(Key),
            Matrix.Select(Key).Order(StringComparer.Ordinal));

        foreach (var entry in Matrix)
        {
            AssertExecutableAndNonSkipped(entry);
        }
    }

    [Fact]
    public void AdapterQualifications_RetainCapabilityEvidenceAndCannotWaiveSemanticConformance()
    {
        foreach (var entry in Matrix.Where(static entry => entry.Profile == ConformanceProfile.AdapterQualification))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(entry.CapabilityEvidence),
                $"{Display(entry)} is adapter-specific but has no attributable capability evidence.");
            Assert.Contains(
                Matrix,
                candidate => candidate.Scenario == entry.Scenario
                    && candidate.Profile == ConformanceProfile.Semantic);
        }
    }

    static ConformanceEntry Semantic(string scenario, Type testClass, string testMethod) =>
        new(scenario, ConformanceProfile.Semantic, testClass, testMethod, CapabilityEvidence: null);

    static ConformanceEntry AdapterQualification(
        string scenario,
        Type testClass,
        string testMethod,
        string capabilityEvidence) =>
        new(
            scenario,
            ConformanceProfile.AdapterQualification,
            testClass,
            testMethod,
            capabilityEvidence);

    static void AssertExecutableAndNonSkipped(ConformanceEntry entry)
    {
        var methods = entry.TestClass
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, entry.TestMethod, StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            methods.Length == 1,
            $"{Display(entry)} resolves to {methods.Length} methods; exactly one executable test is required.");

        var method = methods[0];
        var fact = method.GetCustomAttributes(inherit: true).OfType<FactAttribute>().SingleOrDefault();
        Assert.True(fact is not null, $"{Display(entry)} is not an xUnit Fact or Theory.");

        var skipReasons = method
            .GetCustomAttributes(inherit: true)
            .SelectMany(SkipReasons)
            .ToArray();
        Assert.True(
            skipReasons.Length == 0,
            $"{Display(entry)} is skipped or explicit: {string.Join("; ", skipReasons)}.");
    }

    static IEnumerable<string> SkipReasons(object attribute)
    {
        foreach (var property in attribute.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            if (property.Name is "Skip" or "SkipWhen" or "SkipUnless"
                && property.PropertyType == typeof(string)
                && property.GetValue(attribute) is string value
                && !string.IsNullOrWhiteSpace(value))
            {
                yield return $"{attribute.GetType().Name}.{property.Name}={value}";
            }
            else if (property.Name == "Explicit"
                && property.PropertyType == typeof(bool)
                && property.GetValue(attribute) is true)
            {
                yield return $"{attribute.GetType().Name}.Explicit=true";
            }
        }
    }

    static string Key(ConformanceEntry entry) => string.Concat(
        entry.Scenario,
        '|',
        ((int)entry.Profile).ToString(System.Globalization.CultureInfo.InvariantCulture),
        '|',
        entry.TestClass.FullName,
        '|',
        entry.TestMethod);

    static string Display(ConformanceEntry entry) =>
        $"{entry.Scenario} {entry.Profile} entry {entry.TestClass.FullName}.{entry.TestMethod}";

    enum ConformanceProfile
    {
        Semantic,
        AdapterQualification
    }

    readonly record struct ConformanceEntry(
        string Scenario,
        ConformanceProfile Profile,
        Type TestClass,
        string TestMethod,
        string? CapabilityEvidence);
}
