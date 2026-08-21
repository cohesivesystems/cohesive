using Cohesive.MaterializationHarness.Supervise;

return args.FirstOrDefault() switch
{
    "control-equivalence" => await MaterializationHarnessControlEquivalenceSupervisor.RunAsync(args),
    "source-matrix" => await MaterializationHarnessSupervisor.RunSourceMatrixAsync(args),
    _ => await MaterializationHarnessSupervisor.RunAsync(args)
};
