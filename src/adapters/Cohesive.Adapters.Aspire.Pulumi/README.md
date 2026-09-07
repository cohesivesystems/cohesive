# Cohesive.Adapters.Aspire.Pulumi

This optional adapter connects a completed `Cohesive.Infra` target-deployment plan to Aspire's deployment pipeline and Pulumi's official Automation API.

It has deliberately narrow ownership:

- Cohesive declares requirements, selects target facilities, proves capability discharge, lowers readiness obligations, and retains lifecycle intent and provenance.
- Aspire owns the user-facing `publish`, `deploy`, `destroy`, and `do` pipeline, including progress and summaries.
- Pulumi owns provider reconciliation, stack state, previews/updates, resource outputs, and destruction.

The adapter does not translate every Cohesive node into provider resources, implement an IaC state engine, or reproduce Aspire's pipeline. It hands the exact compiled Cohesive realization to an existing Pulumi program. This is useful while Aspire has no built-in Pulumi deployment target, and leaves a clean replacement seam if one is added later.

## AppHost usage

```csharp
using Cohesive.Adapters.Aspire.Pulumi;

var builder = DistributedApplication.CreateBuilder(args);
var deploymentPlan = AriRemoteInfrastructure.Compile("production");
var repositoryRoot = AriRepository.RootDirectory;

builder.AddCohesivePulumiDeployment(
    name: "ari-production",
    plan: deploymentPlan,
    environmentName: "production",
    pulumiProjectName: "Ari.Infra.Pulumi",
    pulumiStackName: "cohesive/Ari.Infra.Pulumi/production",
    programDirectory: new("infra/src/Ari.Infra.Pulumi"),
    lifecycleAuthority: new("pulumi/ari/production"),
    options: new(repositoryRoot));

builder.Build().Run();
```

The resource contributes three named Aspire pipeline steps:

- handoff materialization, required by Aspire `publish`, `deploy`, and `destroy`;
- Pulumi `up`, required by Aspire `deploy`;
- Pulumi `destroy`, required by Aspire `destroy`.

`aspire publish` writes `cohesive.infra.pulumi.json` as a deterministic one-way artifact. `aspire deploy` and `aspire destroy` materialize the same artifact and invoke the existing Pulumi program through Automation API. The Pulumi project name is validated before a stack is selected or created.

## Pulumi program contract

The Pulumi process receives these non-secret environment variables:

- `COHESIVE_INFRA_HANDOFF_PATH`: absolute path to the complete serialized handoff;
- `COHESIVE_INFRA_HANDOFF_FINGERPRINT`: exact handoff fingerprint;
- `COHESIVE_INFRA_ENVIRONMENT`: selected Cohesive/Aspire environment profile.

The Pulumi program can deserialize the handoff and lower the retained manifest and realization into provider resources. During incremental adoption it can also validate the handoff while existing resource declarations remain in place. Provider configuration and secrets continue to use Pulumi's normal configuration and secrets facilities.

Pulumi output is forwarded to Aspire with Pulumi secret display disabled. Stack outputs are not copied into a second state model; applications should expose only intentionally selected values through explicit integration code.

## Failure behavior

Handoff creation fails before Aspire registers the target when capability witnessing, readiness-obligation lowering, or target deployment diagnostics contain an error. It also rejects exact-fence mismatches and selected-target resources whose lifecycle authority differs from the declared Pulumi stack authority. Existing Cohesive diagnostics remain available on the input `InfrastructureTargetDeploymentPlan` and are retained in successful handoffs.

The Aspire pipeline APIs used by version 13.5 are still marked for evaluation by Aspire. This package isolates that dependency from `Cohesive.Infra` and `Cohesive.Adapters.Aspire`; future Aspire pipeline changes or a native Pulumi deployment target can therefore be absorbed in this adapter without changing the canonical infrastructure IR.
