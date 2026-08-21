using Cohesive.MaterializationHarness.Supervise;

return args.FirstOrDefault() == "control-equivalence"
    ? await MaterializationHarnessControlEquivalenceSupervisor.RunAsync(args)
    : await MaterializationHarnessSupervisor.RunAsync(args);
