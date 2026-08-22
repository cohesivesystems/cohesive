using System.Text.Json;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureBindingElaborationTests
{
    static readonly InfrastructureBindingContractId DurableProcessClient = new("durable-process-client");
    static readonly InfrastructureCapabilityId DurableScheduler = new("durable-process-scheduling");

    [Fact]
    public void Profile_normalization_and_fingerprint_are_independent_of_producer_order()
    {
        InfrastructureBindingContractId artifactClient = new("artifact-client");
        InfrastructureCapabilityId authenticatedAccess = new("authenticated-access");
        InfrastructureCapabilityId artifactStorage = new("artifact-storage");
        var schedulerRule = Rule(
            "rules/durable-process-client/v1",
            DurableProcessClient,
            [DurableScheduler, authenticatedAccess],
            ["spec://scheduler", "spec://authentication"]);
        var artifactRule = Rule(
            "rules/artifact-client/v1",
            artifactClient,
            [artifactStorage],
            ["spec://artifacts"]);

        var first = Profile("profiles/ari-bindings/v1", [schedulerRule, artifactRule]);
        var reordered = Profile(
            "profiles/ari-bindings/v1",
            [
                artifactRule,
                Rule(
                    "rules/durable-process-client/v1",
                    DurableProcessClient,
                    [authenticatedAccess, DurableScheduler],
                    ["spec://authentication", "spec://scheduler"])
            ]);
        var changedAuthority = Profile(
            "profiles/ari-bindings/v1",
            [
                artifactRule,
                Rule(
                    "rules/durable-process-client/v1",
                    DurableProcessClient,
                    [authenticatedAccess, DurableScheduler],
                    ["spec://authentication", "spec://scheduler/v2"])
            ]);
        var tampered = new InfrastructureBindingElaborationProfileFingerprint(
            first.Fingerprint.Algorithm,
            first.Fingerprint.Canonicalization,
            "00");

        Assert.Equal(first, reordered);
        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotEqual(first.Fingerprint, changedAuthority.Fingerprint);
        Assert.Equal(
            ["artifact-client", "durable-process-client"],
            first.Rules.Select(static rule => rule.Contract.Value));
        Assert.Throws<ArgumentException>(() => new InfrastructureBindingElaborationProfile(
            first.SchemaVersion,
            first.Id,
            first.SupportedDefinitionSchemaVersions,
            first.Rules,
            tampered));
    }

    [Fact]
    public void Elaboration_report_is_an_exact_machine_readable_binding_explanation()
    {
        InfrastructureCapabilityId authenticatedAccess = new("authenticated-access");
        var definition = BoundDefinition();
        var profile = Profile(
            "profiles/ari-bindings/v1",
            [
                Rule(
                    "rules/durable-process-client/v1",
                    DurableProcessClient,
                    [DurableScheduler, authenticatedAccess],
                    ["spec://scheduler", "spec://authentication"])
            ]);

        var first = InfrastructureBindingElaborator.Elaborate(definition, profile);
        var second = InfrastructureBindingElaborator.Elaborate(definition, profile);
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(first, options);
        var restored = Assert.IsType<InfrastructureBindingElaborationReport>(
            JsonSerializer.Deserialize<InfrastructureBindingElaborationReport>(json, options));
        var tampered = new InfrastructureBindingElaborationFingerprint(
            first.Fingerprint.Algorithm,
            first.Fingerprint.Canonicalization,
            "00");

        Assert.True(first.IsComplete);
        Assert.Empty(first.Diagnostics);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first, second);
        Assert.Equal(first, restored);
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.Throws<ArgumentException>(() => new InfrastructureBindingElaborationReport(
            first.Definition,
            first.Profile,
            first.Decisions,
            first.Diagnostics,
            tampered));
        Assert.Equal(definition, first.Definition);
        Assert.Equal(profile.ToReference(), first.Profile);

        var decision = Assert.IsType<InfrastructureBindingElaborationDecision>(
            first.FindDecision(new("bindings/jobs/scheduler")));
        Assert.Equal(InfrastructureBindingElaborationStatus.Elaborated, decision.Status);
        Assert.Equal("rules/durable-process-client/v1", Assert.Single(decision.Rules).Value);
        Assert.Equal(2, decision.Obligations.Length);
        Assert.All(decision.Obligations, obligation =>
        {
            Assert.Equal(new InfrastructureBindingId("bindings/jobs/scheduler"), obligation.Binding);
            Assert.Equal(DurableProcessClient, obligation.Contract);
            Assert.Equal("/definition/bindings/0/contract", obligation.Location);
            Assert.StartsWith("binding/bindings%2Fjobs%2Fscheduler/requires/", obligation.Requirement.Id.Value);
        });
        Assert.Equal(
            ["authenticated-access", "durable-process-scheduling"],
            decision.Obligations.Select(static obligation => obligation.Requirement.Capability.Value));
    }

    [Fact]
    public void Unknown_and_competing_contract_authorities_remain_explicit_residuals()
    {
        var definition = BoundDefinition();
        var unavailable = InfrastructureBindingElaborator.Elaborate(
            definition,
            InfrastructureBindingElaborationProfile.Empty);
        var ambiguousProfile = Profile(
            "profiles/ambiguous/v1",
            [
                Rule("rules/scheduler/managed/v1", DurableProcessClient, [DurableScheduler], ["spec://managed"]),
                Rule("rules/scheduler/composed/v1", DurableProcessClient, [DurableScheduler], ["spec://composed"])
            ]);

        var ambiguous = InfrastructureBindingElaborator.Elaborate(definition, ambiguousProfile);

        Assert.False(unavailable.IsComplete);
        Assert.Equal(
            InfrastructureBindingElaborationStatus.Unavailable,
            Assert.Single(unavailable.Decisions).Status);
        var unavailableDiagnostic = Assert.Single(unavailable.Diagnostics);
        Assert.Equal(InfrastructureBindingElaborationDiagnosticCodes.ContractUnavailable, unavailableDiagnostic.Code);
        Assert.Equal("/definition/bindings/0/contract", unavailableDiagnostic.Location);
        Assert.Equal("bindings/jobs/scheduler", unavailableDiagnostic.Evidence?.Subject);
        Assert.Equal("binding contract not elaborated", unavailableDiagnostic.Evidence?.Observed);

        Assert.False(ambiguous.IsComplete);
        var ambiguousDecision = Assert.Single(ambiguous.Decisions);
        Assert.Equal(InfrastructureBindingElaborationStatus.Ambiguous, ambiguousDecision.Status);
        Assert.Equal(2, ambiguousDecision.Rules.Length);
        Assert.Empty(ambiguousDecision.Obligations);
        var ambiguousDiagnostic = Assert.Single(ambiguous.Diagnostics);
        Assert.Equal(InfrastructureBindingElaborationDiagnosticCodes.ContractAmbiguous, ambiguousDiagnostic.Code);
        Assert.Contains("binding-elaboration-rule/rules%2Fscheduler%2Fcomposed%2Fv1", ambiguousDiagnostic.Evidence!.RelatedLocations);
        Assert.Contains("spec://managed", ambiguousDiagnostic.Evidence.SourceReferences);
        Assert.Single(ambiguousDiagnostic.Evidence.ResolutionOptions);
    }

    [Fact]
    public void Binding_derived_requirement_identity_cannot_shadow_an_explicit_requirement()
    {
        InfrastructureBindingId binding = new("bindings/jobs/scheduler");
        var conflictingId = InfrastructureBindingObligation.DeriveRequirementId(binding, DurableScheduler);
        var definition = Infrastructure.Define(new("ari-training"), new("v1"), infrastructure =>
        {
            var jobs = infrastructure.Workload(new("jobs")).Requires(conflictingId, DurableScheduler);
            var scheduler = infrastructure.Resource(new("scheduler")).External();
            infrastructure.Bind(binding, jobs).To(scheduler).As(DurableProcessClient);
        });
        var profile = Profile(
            "profiles/ari-bindings/v1",
            [Rule("rules/durable-process-client/v1", DurableProcessClient, [DurableScheduler], ["spec://scheduler"])]);

        var report = InfrastructureBindingElaborator.Elaborate(definition, profile);

        Assert.False(report.IsComplete);
        Assert.Equal(InfrastructureBindingElaborationStatus.Invalid, Assert.Single(report.Decisions).Status);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal(InfrastructureBindingElaborationDiagnosticCodes.ObligationIdentityConflict, diagnostic.Code);
        Assert.Equal(conflictingId.Value, diagnostic.Evidence?.Observed);
    }

    [Fact]
    public void Ari_contract_set_derives_repository_scheduler_artifact_secret_and_telemetry_obligations()
    {
        var definition = Infrastructure.Define(new("ari-training"), new("contracts-v1"), infrastructure =>
        {
            var jobs = infrastructure.Workload(new("jobs"));
            var state = infrastructure.Resource(new("state")).Persistent();
            var scheduler = infrastructure.Resource(new("scheduler")).External();
            var artifacts = infrastructure.Resource(new("artifacts")).Persistent();
            var secrets = infrastructure.Resource(new("secrets")).External();
            var telemetry = infrastructure.Resource(new("telemetry")).External();

            infrastructure.Bind(new("bindings/jobs/state"), jobs).To(state).As(new("repository-read-write"));
            infrastructure.Bind(new("bindings/jobs/scheduler"), jobs).To(scheduler).As(DurableProcessClient);
            infrastructure.Bind(new("bindings/jobs/artifacts"), jobs).To(artifacts).As(new("object-read-write"));
            infrastructure.Bind(new("bindings/jobs/secrets"), jobs).To(secrets).As(new("secret-consumer"));
            infrastructure.Bind(new("bindings/jobs/telemetry"), jobs).To(telemetry).As(new("telemetry-export"));
        });
        var profile = Profile(
            "profiles/ari-contracts/v1",
            [
                Rule(
                    "rules/repository-read-write/v1",
                    new("repository-read-write"),
                    [new("partitioned-document-storage"), new("optimistic-concurrency")],
                    ["ari://contracts/repository"]),
                Rule(
                    "rules/durable-process-client/v1",
                    DurableProcessClient,
                    [DurableScheduler, new("authenticated-scheduler-client")],
                    ["ari://contracts/process-client"]),
                Rule(
                    "rules/object-read-write/v1",
                    new("object-read-write"),
                    [new("durable-object-storage")],
                    ["ari://contracts/artifacts"]),
                Rule(
                    "rules/secret-consumer/v1",
                    new("secret-consumer"),
                    [new("secret-retrieval"), new("sensitive-configuration")],
                    ["ari://contracts/secrets"]),
                Rule(
                    "rules/telemetry-export/v1",
                    new("telemetry-export"),
                    [new("telemetry-export")],
                    ["ari://contracts/telemetry"])
            ]);

        var report = InfrastructureBindingElaborator.Elaborate(definition, profile);

        Assert.True(report.IsComplete);
        Assert.Empty(report.Diagnostics);
        Assert.Equal(5, report.Decisions.Length);
        Assert.Equal(8, report.Obligations.Length);
        Assert.Equal(
            [
                "authenticated-scheduler-client",
                "durable-object-storage",
                "durable-process-scheduling",
                "optimistic-concurrency",
                "partitioned-document-storage",
                "secret-retrieval",
                "sensitive-configuration",
                "telemetry-export"
            ],
            report.Obligations
                .Select(static obligation => obligation.Requirement.Capability.Value)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Ari_binding_reports_the_missing_scheduler_then_closes_with_exact_evidence()
    {
        InfrastructureCapabilityVariantId production = new("azure-production");
        var definition = BoundDefinition();
        var bindingProfile = Profile(
            "profiles/ari-bindings/v1",
            [Rule("rules/durable-process-client/v1", DurableProcessClient, [DurableScheduler], ["ari://runtime/scheduler-client"])]);
        var withoutScheduler = CapabilityProfile(
            "profiles/azure/without-scheduler/v1",
            production,
            []);
        var withScheduler = CapabilityProfile(
            "profiles/azure/with-scheduler/v1",
            production,
            [
                new(
                    new("evidence/azure-durable-task-scheduler"),
                    DurableScheduler,
                    CapabilityRealizationKind.Native,
                    sourceReferences: ["azure://durable-task-scheduler"])
            ]);

        var missing = InfrastructureCapabilityCompiler.Compile(
            definition,
            withoutScheduler,
            production,
            bindingProfile);
        var complete = InfrastructureCapabilityCompiler.Compile(
            definition,
            withScheduler,
            production,
            bindingProfile);
        var options = StrictDocumentJson.CreateOptions();
        var completeJson = JsonSerializer.Serialize(complete, options);
        using var parsed = JsonDocument.Parse(completeJson);
        var restored = Assert.IsType<InfrastructureCapabilityClosureReport>(
            JsonSerializer.Deserialize<InfrastructureCapabilityClosureReport>(completeJson, options));

        Assert.True(missing.BindingElaboration.IsComplete);
        Assert.False(missing.IsClosed);
        Assert.Single(missing.Decisions);
        Assert.Equal(CapabilityRealizationKind.Unavailable, missing.Decisions[0].Realization);
        var diagnostic = Assert.Single(missing.Diagnostics);
        Assert.Equal(InfrastructureCapabilityDiagnosticCodes.RequirementUnavailable, diagnostic.Code);
        Assert.Equal("/definition/bindings/0/contract", diagnostic.Location);
        Assert.Equal(DurableScheduler.Value, diagnostic.SchemaLocation);
        Assert.Equal(
            InfrastructureBindingObligation.DeriveRequirementId(new("bindings/jobs/scheduler"), DurableScheduler).Value,
            diagnostic.Evidence?.Subject);
        Assert.Contains("binding/bindings%2Fjobs%2Fscheduler", diagnostic.Evidence!.RelatedLocations);
        Assert.Contains("binding-elaboration-rule/rules%2Fdurable-process-client%2Fv1", diagnostic.Evidence.RelatedLocations);
        Assert.Contains("ari://runtime/scheduler-client", diagnostic.Evidence.SourceReferences);
        Assert.DoesNotContain(
            missing.Diagnostics,
            static item => item.Code == InfrastructureBindingElaborationDiagnosticCodes.ContractUnavailable);

        Assert.True(complete.IsClosed);
        Assert.Empty(complete.Diagnostics);
        var decision = Assert.Single(complete.Decisions);
        Assert.Equal(CapabilityRealizationKind.Native, decision.Realization);
        Assert.Equal("evidence/azure-durable-task-scheduler", Assert.Single(decision.Evidence).Value);
        Assert.Equal(
            decision.Requirement,
            Assert.Single(complete.BindingElaboration.Obligations).Requirement.Id);
        Assert.Equal(decision, complete.FindDecision(decision.Requirement));
        Assert.False(parsed.RootElement.TryGetProperty("definition", out _));
        Assert.True(parsed.RootElement.GetProperty("bindingElaboration").TryGetProperty("definition", out _));
        Assert.Equal(complete, restored);
        Assert.Equal(definition, restored.Definition);
    }

    static InfrastructureDefinitionDocument BoundDefinition() =>
        Infrastructure.Define(new("ari-training"), new("v1"), infrastructure =>
        {
            var jobs = infrastructure.Workload(new("jobs"));
            var scheduler = infrastructure.Resource(new("scheduler")).External();
            infrastructure.Bind(new("bindings/jobs/scheduler"), jobs).To(scheduler).As(DurableProcessClient);
        });

    static InfrastructureBindingElaborationProfile Profile(
        string id,
        InfrastructureBindingElaborationRule[] rules) => new(
            InfrastructureBindingElaborationProfile.CurrentSchemaVersion,
            new(id),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [.. rules]);

    static InfrastructureBindingElaborationRule Rule(
        string id,
        InfrastructureBindingContractId contract,
        InfrastructureCapabilityId[] capabilities,
        string[] sources) => new(
            new(id),
            contract,
            [.. capabilities],
            [.. sources]);

    static InfrastructureCapabilityProfile CapabilityProfile(
        string id,
        InfrastructureCapabilityVariantId variant,
        InfrastructureCapabilityEvidence[] evidence) => new(
            InfrastructureCapabilityProfile.CurrentSchemaVersion,
            new(id),
            new("azure"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [new InfrastructureCapabilityVariant(variant, evidence: [.. evidence])]);
}
