using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureCapabilityCompilerTests
{
    const string DurableScheduler = "durable-process-scheduling";

    [Fact]
    public void Binding_contracts_fail_closed_until_their_induced_obligations_are_elaborated()
    {
        InfrastructureCapabilityVariantId variant = new("local");
        var definition = Infrastructure.Define(new("bound-system"), new("v1"), infrastructure =>
        {
            var api = infrastructure.Workload(new("api"));
            var store = infrastructure.Resource(new("store")).Persistent();
            infrastructure.Bind(new("bindings/api/store"), api)
                .To(store)
                .As(new("document-read-write"));
        });
        var profile = Profile("empty-target", new InfrastructureCapabilityVariant(variant));

        var report = InfrastructureCapabilityCompiler.Compile(definition, profile, variant);

        Assert.False(report.IsClosed);
        Assert.Empty(report.Decisions);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal(InfrastructureCapabilityDiagnosticCodes.BindingElaborationUnavailable, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("/definition/bindings/0/contract", diagnostic.Location);
        Assert.Equal("document-read-write", diagnostic.SchemaLocation);
        var evidence = Assert.IsType<Cohesive.Model.Serialization.DocumentDiagnosticEvidence>(diagnostic.Evidence);
        Assert.Equal("infrastructure-binding-elaboration", evidence.Stage);
        Assert.Equal("bindings/api/store", evidence.Subject);
        Assert.Equal("binding contract not elaborated", evidence.Observed);
        Assert.StartsWith("bound-system@v1#sha256:", Assert.Single(evidence.SourceReferences), StringComparison.Ordinal);
        Assert.Equal(2, evidence.ResolutionOptions.Length);
    }

    [Fact]
    public void Reusable_target_profiles_reject_demand_scoped_override_evidence()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InfrastructureCapabilityEvidence(
                new("evidence/override"),
                new(DurableScheduler),
                CapabilityRealizationKind.Override,
                configuration:
                [
                    new(
                        "requirements/jobs/durable-scheduler",
                        EffectiveConfigurationOrigin.Explicit,
                        "definition/v1")
                ],
                sourceReferences: ["test://explicit-override"]));

        Assert.Equal("realization", exception.ParamName);
    }

    [Fact]
    public void Closure_report_retains_the_exact_capability_profile_fingerprint()
    {
        InfrastructureCapabilityVariantId variant = new("production");
        InfrastructureCapabilityId capability = new(DurableScheduler);
        var first = ProfileWithId(
            "azure",
            "profiles/azure/v1",
            new InfrastructureCapabilityVariant(
                variant,
                evidence:
                [
                    new(
                        new("evidence/scheduler"),
                        capability,
                        CapabilityRealizationKind.Native,
                        sourceReferences: ["provider://scheduler/version-1"])
                ]));
        var changedUnderSameId = ProfileWithId(
            "azure",
            "profiles/azure/v1",
            new InfrastructureCapabilityVariant(
                variant,
                evidence:
                [
                    new(
                        new("evidence/scheduler"),
                        capability,
                        CapabilityRealizationKind.Native,
                        sourceReferences: ["provider://scheduler/version-2"])
                ]));

        var report = InfrastructureCapabilityCompiler.Compile(SchedulerDefinition(), first, variant);
        var tampered = new InfrastructureCapabilityProfileFingerprint(
            first.Fingerprint.Algorithm,
            first.Fingerprint.Canonicalization,
            "00");

        Assert.NotEqual(first.Fingerprint, changedUnderSameId.Fingerprint);
        Assert.Equal(first.ToReference(), report.Profile);
        Assert.Throws<ArgumentException>(() => new InfrastructureCapabilityProfile(
            first.SchemaVersion,
            first.Id,
            first.Target,
            first.SupportedDefinitionSchemaVersions,
            first.Variants,
            tampered));
    }

    [Fact]
    public void Compiler_input_diagnostic_locations_are_relative_to_their_exact_source_documents()
    {
        InfrastructureCapabilityVariantId available = new("available");
        InfrastructureCapabilityVariantId requested = new("requested");
        var profile = new InfrastructureCapabilityProfile(
            "cohesive.infra.capabilities/future",
            new("profiles/source-roots/v1"),
            new("source-root-target"),
            ["cohesive-infrastructure/future"],
            [new InfrastructureCapabilityVariant(available)]);

        var report = InfrastructureCapabilityCompiler.Compile(SchedulerDefinition(), profile, requested);

        var profileSchema = Assert.Single(report.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.ProfileSchemaUnsupported));
        Assert.Equal("/schemaVersion", profileSchema.Location);
        Assert.StartsWith(
            "profiles/source-roots/v1#sha256:",
            Assert.Single(profileSchema.Evidence!.SourceReferences),
            StringComparison.Ordinal);

        var definitionSchema = Assert.Single(report.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.DefinitionSchemaUnsupported));
        Assert.Equal("/schemaVersion", definitionSchema.Location);
        Assert.Contains(
            definitionSchema.Evidence!.SourceReferences,
            static source => source.StartsWith("ari-scheduler@v1#sha256:", StringComparison.Ordinal));

        var variant = Assert.Single(report.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.VariantUnavailable));
        Assert.Equal("/variants", variant.Location);
        Assert.Equal("requested", variant.Evidence?.Subject);
    }

    [Fact]
    public void Coherent_variants_cannot_combine_partial_evidence()
    {
        InfrastructureCapabilityId queue = new("durable-command-queue");
        InfrastructureCapabilityId timer = new("durable-timer");
        InfrastructureCapabilityId scheduler = new(DurableScheduler);
        var schedulerRule = new InfrastructureCapabilityRule(
            new("rules/scheduler-from-queue-and-timer/v1"),
            scheduler,
            [queue, timer]);
        InfrastructureCapabilityVariantId queueOnly = new("queue-only");
        InfrastructureCapabilityVariantId timerOnly = new("timer-only");
        var profile = Profile(
            "split-target",
            new(
                queueOnly,
                evidence: [NativeEvidence("evidence/queue", queue)],
                rules: [schedulerRule]),
            new(
                timerOnly,
                evidence: [NativeEvidence("evidence/timer", timer)],
                rules: [schedulerRule]));
        var definition = SchedulerDefinition();

        var queueReport = InfrastructureCapabilityCompiler.Compile(definition, profile, queueOnly);
        var timerReport = InfrastructureCapabilityCompiler.Compile(definition, profile, timerOnly);

        AssertUnavailable(queueReport);
        AssertUnavailable(timerReport);
    }

    [Fact]
    public void Recursive_and_rules_and_alternative_or_rules_produce_one_complete_proof()
    {
        InfrastructureCapabilityId queue = new("durable-command-queue");
        InfrastructureCapabilityId lease = new("exclusive-lease");
        InfrastructureCapabilityId timer = new("durable-timer");
        InfrastructureCapabilityId commandLog = new("durable-command-log");
        InfrastructureCapabilityId managedScheduler = new("managed-durable-scheduler");
        InfrastructureCapabilityId scheduler = new(DurableScheduler);
        InfrastructureCapabilityRuleId commandLogRule = new("rules/command-log/v1");
        InfrastructureCapabilityRuleId schedulerRule = new("rules/scheduler-composed/v1");
        InfrastructureCapabilityVariantId variant = new("composed");
        var profile = Profile(
            "composition-target",
            new InfrastructureCapabilityVariant(
                variant,
                evidence:
                [
                    NativeEvidence("evidence/queue", queue),
                    NativeEvidence("evidence/lease", lease),
                    NativeEvidence("evidence/timer", timer)
                ],
                rules:
                [
                    new(commandLogRule, commandLog, [queue, lease]),
                    new(new("rules/scheduler-managed/v1"), scheduler, [managedScheduler]),
                    new(schedulerRule, scheduler, [commandLog, timer])
                ]));

        var report = InfrastructureCapabilityCompiler.Compile(SchedulerDefinition(), profile, variant);

        Assert.True(report.IsClosed);
        Assert.Empty(report.Diagnostics);
        var decision = Assert.Single(report.Decisions);
        Assert.Equal(CapabilityRealizationKind.Composed, decision.Realization);
        Assert.Equal(
            ["evidence/lease", "evidence/queue", "evidence/timer"],
            decision.Evidence.Select(static value => value.Value));
        Assert.Equal(
            [commandLogRule.Value, schedulerRule.Value],
            decision.Rules.Select(static value => value.Value));
    }

    [Fact]
    public void Competing_complete_or_proofs_are_diagnosed_as_ambiguous()
    {
        InfrastructureCapabilityId scheduler = new(DurableScheduler);
        InfrastructureCapabilityId queue = new("durable-command-queue");
        InfrastructureCapabilityVariantId variant = new("ambiguous");
        var profile = Profile(
            "ambiguous-target",
            new InfrastructureCapabilityVariant(
                variant,
                evidence:
                [
                    NativeEvidence("evidence/managed-scheduler", scheduler),
                    NativeEvidence("evidence/queue", queue)
                ],
                rules: [new(new("rules/scheduler-from-queue/v1"), scheduler, [queue])]));

        var report = InfrastructureCapabilityCompiler.Compile(SchedulerDefinition(), profile, variant);

        Assert.False(report.IsClosed);
        var decision = Assert.Single(report.Decisions);
        Assert.Equal(CapabilityRealizationKind.Unknown, decision.Realization);
        Assert.Empty(decision.Evidence);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal(InfrastructureCapabilityDiagnosticCodes.RequirementAmbiguous, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("/definition/workloads/0/requirements/0/capability", diagnostic.Location);
        Assert.Equal(DurableScheduler, diagnostic.SchemaLocation);
        var evidence = Assert.IsType<Cohesive.Model.Serialization.DocumentDiagnosticEvidence>(diagnostic.Evidence);
        Assert.Equal("infrastructure-capability-matching", evidence.Stage);
        Assert.Equal("requirements/jobs/durable-scheduler", evidence.Subject);
        Assert.Equal(DurableScheduler, evidence.Expected);
        Assert.Equal("ambiguous", evidence.Observed);
        Assert.Contains("capability-evidence/evidence%2Fmanaged-scheduler", evidence.RelatedLocations);
        Assert.Contains("capability-rule/rules%2Fscheduler-from-queue%2Fv1", evidence.RelatedLocations);
        Assert.Contains("test://evidence/managed-scheduler", evidence.SourceReferences);
        Assert.Single(evidence.ResolutionOptions);
    }

    [Fact]
    public void Recursive_capability_rules_are_diagnosed_as_a_composition_cycle()
    {
        InfrastructureCapabilityId scheduler = new(DurableScheduler);
        InfrastructureCapabilityId schedulerState = new("durable-scheduler-state");
        InfrastructureCapabilityVariantId variant = new("cyclic-rules");
        var profile = Profile(
            "cyclic-rule-target",
            new InfrastructureCapabilityVariant(
                variant,
                rules:
                [
                    new(new("rules/scheduler/v1"), scheduler, [schedulerState]),
                    new(new("rules/scheduler-state/v1"), schedulerState, [scheduler])
                ]));

        var report = InfrastructureCapabilityCompiler.Compile(SchedulerDefinition(), profile, variant);

        Assert.False(report.IsClosed);
        Assert.Equal(CapabilityRealizationKind.Unknown, Assert.Single(report.Decisions).Realization);
        Assert.Equal(
            InfrastructureCapabilityDiagnosticCodes.CompositionCycle,
            Assert.Single(report.Diagnostics).Code);
    }

    [Fact]
    public void Recursive_auxiliary_evidence_is_diagnosed_as_an_evidence_cycle()
    {
        InfrastructureCapabilityId scheduler = new(DurableScheduler);
        InfrastructureCapabilityId schedulerState = new("durable-scheduler-state");
        InfrastructureCapabilityEvidenceId schedulerEvidence = new("evidence/scheduler");
        InfrastructureCapabilityEvidenceId stateEvidence = new("evidence/scheduler-state");
        InfrastructureCapabilityVariantId variant = new("cyclic-evidence");
        var profile = Profile(
            "cyclic-evidence-target",
            new InfrastructureCapabilityVariant(
                variant,
                evidence:
                [
                    new(
                        schedulerEvidence,
                        scheduler,
                        CapabilityRealizationKind.Composed,
                        auxiliaries: [stateEvidence],
                        sourceReferences: ["test://scheduler-evidence"]),
                    new(
                        stateEvidence,
                        schedulerState,
                        CapabilityRealizationKind.Composed,
                        auxiliaries: [schedulerEvidence],
                        sourceReferences: ["test://scheduler-state-evidence"])
                ]));

        var report = InfrastructureCapabilityCompiler.Compile(SchedulerDefinition(), profile, variant);

        Assert.False(report.IsClosed);
        Assert.Equal(CapabilityRealizationKind.Unknown, Assert.Single(report.Decisions).Realization);
        Assert.Equal(InfrastructureCapabilityDiagnosticCodes.EvidenceCycle, Assert.Single(report.Diagnostics).Code);
    }

    [Fact]
    public void Ari_azure_production_reports_a_missing_scheduler_then_closes_when_evidence_is_added()
    {
        InfrastructureCapabilityVariantId production = new("azure-production");
        var withoutScheduler = ProfileWithId(
            "azure-production",
            "profiles/azure-production/without-scheduler/v1",
            new InfrastructureCapabilityVariant(
                production,
                evidence:
                [
                    NativeEvidence(
                        "evidence/cosmos-document-store",
                        new("partitioned-document-storage"))
                ]));
        var withScheduler = ProfileWithId(
            "azure-production",
            "profiles/azure-production/with-scheduler/v1",
            new InfrastructureCapabilityVariant(
                production,
                evidence:
                [
                    NativeEvidence(
                        "evidence/azure-durable-task-scheduler",
                        new(DurableScheduler))
                ]));
        var definition = SchedulerDefinition();

        var missing = InfrastructureCapabilityCompiler.Compile(definition, withoutScheduler, production);
        var complete = InfrastructureCapabilityCompiler.Compile(definition, withScheduler, production);

        AssertUnavailable(missing);
        var diagnostic = Assert.Single(missing.Diagnostics);
        Assert.Equal(InfrastructureCapabilityDiagnosticCodes.RequirementUnavailable, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("/definition/workloads/0/requirements/0/capability", diagnostic.Location);
        Assert.Equal(DurableScheduler, diagnostic.SchemaLocation);
        var evidence = Assert.IsType<Cohesive.Model.Serialization.DocumentDiagnosticEvidence>(diagnostic.Evidence);
        Assert.Equal("infrastructure-capability-matching", evidence.Stage);
        Assert.Equal("requirements/jobs/durable-scheduler", evidence.Subject);
        Assert.Equal(DurableScheduler, evidence.Expected);
        Assert.Equal("unavailable", evidence.Observed);
        Assert.StartsWith(
            "profiles/azure-production/without-scheduler/v1#sha256:",
            Assert.Single(evidence.SourceReferences),
            StringComparison.Ordinal);
        Assert.Equal(2, evidence.ResolutionOptions.Length);

        Assert.True(complete.IsClosed);
        Assert.Empty(complete.Diagnostics);
        var decision = Assert.Single(complete.Decisions);
        Assert.Equal(CapabilityRealizationKind.Native, decision.Realization);
        Assert.Equal("evidence/azure-durable-task-scheduler", Assert.Single(decision.Evidence).Value);
    }

    [Fact]
    public void Ari_aspire_local_support_remains_residual_until_its_boundary_is_accepted()
    {
        InfrastructureOperatingBoundaryId boundaryId = new("boundaries/process-lifetime-only");
        InfrastructureCapabilityVariantId local = new("aspire-local");
        var boundary = new InfrastructureOperatingBoundary(
            boundaryId,
            "Scheduling durability is limited to the lifetime of the local development process.",
            sourceReferences: ["ari/AspireHost"]);
        var profile = Profile(
            "aspire-local",
            new InfrastructureCapabilityVariant(
                local,
                evidence:
                [
                    new(
                        new("evidence/aspire-process-scheduler"),
                        new(DurableScheduler),
                        CapabilityRealizationKind.Constrained,
                        operatingBoundaries: [boundaryId],
                        sourceReferences: ["ari/AspireHost"])
                ],
                operatingBoundaries: [boundary]));

        var report = InfrastructureCapabilityCompiler.Compile(SchedulerDefinition(), profile, local);

        Assert.False(report.IsClosed);
        var decision = Assert.Single(report.Decisions);
        Assert.Equal(CapabilityRealizationKind.Constrained, decision.Realization);
        Assert.Equal(boundaryId, Assert.Single(decision.OperatingBoundaries));
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal(
            InfrastructureCapabilityDiagnosticCodes.OperatingBoundaryAcceptanceRequired,
            diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("/definition/workloads/0/requirements/0/capability", diagnostic.Location);
        var evidence = Assert.IsType<Cohesive.Model.Serialization.DocumentDiagnosticEvidence>(diagnostic.Evidence);
        Assert.Equal("infrastructure-capability-matching", evidence.Stage);
        Assert.Equal("requirements/jobs/durable-scheduler", evidence.Subject);
        Assert.Equal("constrained proof with unaccepted operating boundaries", evidence.Observed);
        Assert.Contains("capability-evidence/evidence%2Faspire-process-scheduler", evidence.RelatedLocations);
        Assert.Contains("operating-boundary/boundaries%2Fprocess-lifetime-only", evidence.RelatedLocations);
        Assert.Contains("ari/AspireHost", evidence.SourceReferences);
        Assert.Equal(3, evidence.ResolutionOptions.Length);
    }

    [Fact]
    public void Resource_requirement_diagnostics_use_the_canonical_resource_pointer()
    {
        InfrastructureCapabilityVariantId variant = new("empty");
        var definition = Infrastructure.Define(new("resource-system"), new("v1"), infrastructure =>
            infrastructure.Resource(new("scheduler"))
                .Persistent()
                .Requires(new("requirements/scheduler/durability"), new(DurableScheduler)));

        var report = InfrastructureCapabilityCompiler.Compile(
            definition,
            Profile("empty-target", new InfrastructureCapabilityVariant(variant)),
            variant);

        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal(InfrastructureCapabilityDiagnosticCodes.RequirementUnavailable, diagnostic.Code);
        Assert.Equal("/definition/resources/0/requirements/0/capability", diagnostic.Location);
        Assert.Equal("requirements/scheduler/durability", diagnostic.Evidence?.Subject);
    }

    [Fact]
    public void Ari_pulumi_and_terraform_variants_independently_close_the_same_definition()
    {
        InfrastructureCapabilityVariantId pulumi = new("pulumi");
        InfrastructureCapabilityVariantId terraform = new("terraform");
        var profile = Profile(
            "azure-production",
            new(
                pulumi,
                evidence:
                [
                    NativeEvidence(
                        "evidence/pulumi/durable-task-scheduler",
                        new(DurableScheduler))
                ]),
            new(
                terraform,
                evidence:
                [
                    NativeEvidence(
                        "evidence/terraform/durable-task-scheduler",
                        new(DurableScheduler))
                ]));
        var definition = SchedulerDefinition();

        var pulumiReport = InfrastructureCapabilityCompiler.Compile(definition, profile, pulumi);
        var terraformReport = InfrastructureCapabilityCompiler.Compile(definition, profile, terraform);

        Assert.True(pulumiReport.IsClosed);
        Assert.True(terraformReport.IsClosed);
        Assert.Equal(
            "evidence/pulumi/durable-task-scheduler",
            Assert.Single(Assert.Single(pulumiReport.Decisions).Evidence).Value);
        Assert.Equal(
            "evidence/terraform/durable-task-scheduler",
            Assert.Single(Assert.Single(terraformReport.Decisions).Evidence).Value);
    }

    static InfrastructureDefinitionDocument SchedulerDefinition() =>
        Infrastructure.Define(new("ari-scheduler"), new("v1"), infrastructure =>
            infrastructure.Workload(new("jobs"))
                .Requires(new("requirements/jobs/durable-scheduler"), new(DurableScheduler)));

    static InfrastructureCapabilityProfile Profile(
        string target,
        params InfrastructureCapabilityVariant[] variants) => ProfileWithId(
            target,
            $"profiles/{target}/v1",
            variants);

    static InfrastructureCapabilityProfile ProfileWithId(
        string target,
        string profileId,
        params InfrastructureCapabilityVariant[] variants) => new(
            InfrastructureCapabilityProfile.CurrentSchemaVersion,
            new(profileId),
            new(target),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [.. variants]);

    static InfrastructureCapabilityEvidence NativeEvidence(
        string id,
        InfrastructureCapabilityId capability) => new(
            new(id),
            capability,
            CapabilityRealizationKind.Native,
            sourceReferences: [$"test://{id}"]);

    static void AssertUnavailable(InfrastructureCapabilityClosureReport report)
    {
        Assert.False(report.IsClosed);
        var decision = Assert.Single(report.Decisions);
        Assert.Equal(CapabilityRealizationKind.Unavailable, decision.Realization);
        Assert.Empty(decision.Evidence);
        Assert.Contains(
            report.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.RequirementUnavailable);
    }
}
