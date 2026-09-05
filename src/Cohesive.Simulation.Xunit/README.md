# Cohesive.Simulation.Xunit

`Cohesive.Simulation.Xunit` is the optional xUnit assertion adapter for runner-neutral property checks from
`Cohesive.Simulation`. It reports existing `PropertyCaseRunResult` values; it does not own generation, evaluation,
shrinking, or replay semantics.

## Install

The current alpha targets .NET 10 and xUnit 2:

```bash
dotnet add package Cohesive.Simulation.Xunit --prerelease
```

```csharp
using Cohesive.Simulation;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Xunit;
using Xunit;

[Fact]
public void Customer_AlwaysHasAnAdultAge()
{
    var customers = Simulation.Define<Customer>(customer => customer
        .Member(value => value.Age, Gen.Int32(minimum: 0, maximum: 100)))
        .Compile();

    PropertyCaseRunResult result = customers.CheckProperty(
        seed: 42,
        property: static customer => customer.Age >= 18);

    PropertyCaseAssert.Passed(result);
}

public sealed record Customer(int Age);
```

Passing results return normally. Counterexample, invalid, and exhausted results throw `XunitException` with stable
status, bounded-run counts, normalized coverage, structured diagnostics, and any best counterexample. Counterexample
reports retain the exact `csimpc1` replay token. The canonical observation is included for inspection but capped at
4,096 characters with an explicit truncation marker, so large generated values do not overwhelm test and agent logs.

The assertion accepts only a completed result by design. Keep property callbacks in the runner-neutral check so the
same result can be inspected by scripts, other test runners, and future assurance tooling without making xUnit a
second semantic authority.

The runner-neutral result can also be consumed directly by scripts or another test adapter. See the
[getting-started guide](https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Simulation/docs/getting-started.md)
for replay and bounded-run behavior.
