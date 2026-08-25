using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Processes.IR;
using Cohesive.Processes.Runtime;
using CohesiveApi = Cohesive.Api.Api;

namespace Cohesive.Processes.Contracts;

/// <summary>Code-generation roots for the frontend Cohesive Process contract package.</summary>
public static class ProcessesContractsDefinition
{
    /// <summary>
    /// API definition used only to expose the canonical Process payload and portable observation evidence to
    /// contract code generation.
    /// </summary>
    [ApiDefinition]
    public static ApiDefinition Definition { get; } = ApiDefinition.From(
        CohesiveApi
            .Define("ProcessesContracts")
            .Action("ProcessDefinition")
            .Route("GET", "/processes/contracts/process-definition")
            .Returns<ProcessDefinition>()
            .Build(),
        CohesiveApi
            .Define("ProcessesContracts")
            .Action("ExecutionDefinitionDocument")
            .Route("GET", "/processes/contracts/execution-definition-document")
            .Returns<ExecutionDefinitionDocument>()
            .Build(),
        CohesiveApi
            .Define("ProcessesContracts")
            .Action("ExecutionStatus")
            .Route("GET", "/processes/contracts/execution-status")
            .Returns<ExecutionStatus>()
            .Build(),
        CohesiveApi
            .Define("ProcessesContracts")
            .Action("ExecutionControlResult")
            .Route("GET", "/processes/contracts/execution-control-result")
            .Returns<ExecutionControlResult>()
            .Build(),
        CohesiveApi
            .Define("ProcessesContracts")
            .Action("ExecutionExplainArtifact")
            .Route("GET", "/processes/contracts/execution-explain-artifact")
            .Returns<ExecutionExplainArtifact>()
            .Build(),
        CohesiveApi
            .Define("ProcessesContracts")
            .Action("ProcessExecutionTraceArtifact")
            .Route("GET", "/processes/contracts/execution-trace-artifact")
            .Returns<ProcessExecutionTraceArtifact>()
            .Build()
    );
}
