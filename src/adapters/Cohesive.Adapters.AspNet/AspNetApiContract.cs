using System.Text.Json;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model;
using Microsoft.AspNetCore.Mvc;

namespace Cohesive.Adapters.AspNet;

/// <summary>Lowers API contracts with the ASP.NET ProblemDetails wire interpretation.</summary>
public static class AspNetApiContract
{
    /// <summary>Lowers an endpoint under an explicit serializer and adapter profile.</summary>
    /// <param name="operation">Authoritative API declaration.</param>
    /// <param name="jsonOptions">Effective serializer options.</param>
    /// <param name="serializerProfile">Versioned effective serializer profile, including this interpretation.</param>
    /// <param name="execution">Optional exact execution binding.</param>
    /// <param name="service">Optional exact service binding.</param>
    /// <param name="serviceOperation">Operation selected from the service.</param>
    /// <returns>A portable contract; converter objects and CLR metadata do not survive lowering.</returns>
    /// <exception cref="ArgumentNullException">Operation or options is null.</exception>
    /// <exception cref="ArgumentException">Profile is empty or service binding is inconsistent.</exception>
    /// <exception cref="NotSupportedException">Serializer metadata cannot be lowered safely.</exception>
    /// <exception cref="InvalidOperationException">Lowered wire types declare conflicting identities.</exception>
    public static ApiContractDocument Lower(ApiOperation operation, JsonSerializerOptions jsonOptions, string serializerProfile,
        ExecutionDefinitionReference? execution = null, ExecutionDefinitionReference? service = null, string? serviceOperation = null) =>
        ApiContractDocument.Lower(operation, jsonOptions, serializerProfile, execution, service, serviceOperation, ProblemWire.Instance);

    // ASP.NET's attributed ProblemDetails converter has five optional standard members and open extension data.
    // The tolerant-reader policy permits extra keys; it does not assign a type to application-defined extensions.
    sealed class ProblemWire : IClrShapeMetadataProvider
    {
        internal static readonly ProblemWire Instance = new();
        static readonly ObjectTypeRef Contract = new([
            Field("type", ScalarTypeKind.String), Field("title", ScalarTypeKind.String),
            Field("status", ScalarTypeKind.Int32), Field("detail", ScalarTypeKind.String), Field("instance", ScalarTypeKind.String)
        ]);
        static ObjectFieldTypeDef Field(string name, ScalarTypeKind kind) =>
            new(name, new ScalarTypeRef(kind), presence: FieldPresence.Optional, nullability: FieldNullability.Nullable);
        public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context) =>
            context.Target is ClrShapeMetadataTarget.Type or ClrShapeMetadataTarget.Field && context.ClrType == typeof(ProblemDetails)
                ? new() { TypeRef = Contract } : ClrShapeMetadata.Empty;
    }
}
