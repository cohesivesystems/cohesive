using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Control;
using Cohesive.Execution;

namespace Cohesive.Tests.Api;

public sealed class ExecutionControlApiCatalogTests
{
    [Fact]
    public void CanonicalCatalog_DeclaresExactlyNineTransportNeutralOperationsInStableOrder()
    {
        var catalog = ExecutionControlApiCatalog.Create();
        ApiEndpoint[] endpoints =
        [
            catalog.Start,
            catalog.Inspect,
            catalog.Signal,
            catalog.Pause,
            catalog.Continue,
            catalog.RestartAttempt,
            catalog.Cancel,
            catalog.Terminate,
            catalog.UpdateLimits
        ];

        Assert.Equal(9, catalog.Definition.Operations.Count);
        Assert.Equal("cohesive-execution-control-api/v2", ExecutionControlApiCatalog.CurrentSchemaVersion.Value);
        Assert.Equal(endpoints, catalog.Definition.Endpoints);
        Assert.Equal(
            [
                ProcessStartWireNames.Start,
                ExecutionControlWireNames.Inspect,
                ExecutionControlWireNames.Signal,
                ExecutionControlWireNames.Pause,
                ExecutionControlWireNames.Continue,
                ExecutionControlWireNames.RestartAttempt,
                ExecutionControlWireNames.Cancel,
                ExecutionControlWireNames.Terminate,
                ControlLimitUpdateWireNames.UpdateLimits
            ],
            catalog.Definition.Operations.Select(static operation => operation.Name));
        Assert.Equal(
            [
                typeof(ProcessStartRequest),
                typeof(InspectProcessCommand),
                typeof(SignalProcessCommand),
                typeof(PauseProcessCommand),
                typeof(ContinueProcessCommand),
                typeof(RestartProcessAttemptCommand),
                typeof(CancelProcessCommand),
                typeof(TerminateProcessCommand),
                typeof(ControlLimitUpdateCommand)
            ],
            catalog.Definition.Operations.Select(static operation => operation.RequestType));
        Assert.Equal(
            catalog.Definition.Operations.Count,
            catalog.Definition.Operations.Select(static operation => operation.Id).Distinct().Count());
        Assert.All(catalog.Definition.Operations, static operation => Assert.Null(operation.Http));
        Assert.All(
            catalog.Definition.Operations.SelectMany(static operation => operation.Results),
            static result => Assert.Null(result.Http));
        var updateLimits = catalog.Definition.GetOperation(catalog.UpdateLimits);
        Assert.Equal(
            [
                ApiResultKind.Success,
                ApiResultKind.Accepted,
                ApiResultKind.PreconditionFailed,
                ApiResultKind.Conflict,
                ApiResultKind.ValidationFailed,
                ApiResultKind.Forbidden,
                ApiResultKind.NotFound
            ],
            updateLimits.Results.Select(static result => result.Kind));

        var httpProjection = updateLimits.WithHttp(new HttpBinding(
            method: "POST",
            route: "/execution-control/limits",
            parameters: null,
            body: null));
        var accepted = Assert.Single(
            httpProjection.Results,
            static result => result.Kind == ApiResultKind.Accepted);
        Assert.Equal(202, accepted.Http?.StatusCode);
    }

    [Fact]
    public void EveryOperation_RetainsStableAuthorizationAndApiPlusKernelSemanticReferences()
    {
        var catalog = ExecutionControlApiCatalog.Create();

        foreach (var operation in catalog.Definition.Operations)
        {
            var requirement = Assert.Single(operation.AuthorizationRequirements);
            Assert.Equal(ExecutionControlApiWireNames.AuthorizationRequirement(operation.Name), requirement.Id);

            Assert.Collection(
                operation.SemanticReferences,
                api =>
                {
                    Assert.Equal(ExecutionControlApiWireNames.SemanticAuthority, api.Authority);
                    Assert.Equal(ExecutionControlApiCatalog.CurrentSchemaVersion, api.SchemaVersion);
                    Assert.Equal(ExecutionControlApiWireNames.OperationPath(operation.Name), api.Path);
                },
                kernel =>
                {
                    var isStart = operation.Name == ProcessStartWireNames.Start;
                    var isLimitUpdate = operation.Name == ControlLimitUpdateWireNames.UpdateLimits;
                    Assert.Equal(
                        isStart
                            ? ProcessStartWireNames.SemanticAuthority
                            : isLimitUpdate
                                ? ControlLimitUpdateWireNames.SemanticAuthority
                                : ExecutionControlWireNames.SemanticAuthority,
                        kernel.Authority);
                    Assert.Equal(
                        isStart
                            ? ProcessStartRequest.CurrentSchemaVersion
                            : isLimitUpdate
                                ? ControlLoopDefinition.CurrentSchemaVersion
                                : ProcessControlCommand.CurrentSchemaVersion,
                        kernel.SchemaVersion);
                    var expectedPath = isStart
                        ? ProcessStartWireNames.RequestPath
                        : isLimitUpdate
                            ? ControlLimitUpdateWireNames.CommandPath
                            : ExecutionControlWireNames.CommandPath(operation.Name);
                    Assert.Equal(expectedPath, kernel.Path);
                });
        }
    }
}
