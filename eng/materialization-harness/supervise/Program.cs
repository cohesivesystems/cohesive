using Cohesive.MaterializationHarness.Supervise;

return args.FirstOrDefault() switch
{
    "catalog" => MaterializationHarnessMatrixProgram.PrintCatalog(args),
    "aggregate-manifest" => await MaterializationHarnessMatrixProgram.WriteAggregateManifestAsync(args),
    "compatibility-drift" => await MaterializationHarnessSupervisor.RunCompatibilityDriftAsync(args),
    "control-equivalence" => await MaterializationHarnessControlEquivalenceSupervisor.RunAsync(args),
    "elastic-failure" => await MaterializationHarnessSupervisor.RunElasticFailureAsync(args),
    "source-matrix" => await MaterializationHarnessSupervisor.RunSourceMatrixAsync(args),
    _ => await MaterializationHarnessSupervisor.RunAsync(args)
};
