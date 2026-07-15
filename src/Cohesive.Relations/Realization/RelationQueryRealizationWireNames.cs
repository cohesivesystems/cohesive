namespace Cohesive.Relations.Realization;

/// <summary>Stable JSON discriminator property names and values for portable realization contracts.</summary>
public static class RelationQueryRealizationWireNames
{
    /// <summary>Discriminator property used by <see cref="RelationQueryCapability"/>.</summary>
    public const string CapabilityDiscriminator = "$capability";

    /// <summary>Discriminator value for <see cref="LogicalRelationQueryCapability"/>.</summary>
    public const string LogicalCapability = "logical";

    /// <summary>Discriminator value for <see cref="ExpressionRelationQueryCapability"/>.</summary>
    public const string ExpressionCapability = "expression";

    /// <summary>Discriminator value for <see cref="TemporalRelationQueryCapability"/>.</summary>
    public const string TemporalCapability = "temporal";

    /// <summary>Discriminator value for <see cref="StructuralRelationQueryCapability"/>.</summary>
    public const string StructuralCapability = "structural";

    /// <summary>Discriminator value for <see cref="GuaranteeRelationQueryCapability"/>.</summary>
    public const string GuaranteeCapability = "guarantee";

    /// <summary>Discriminator value for <see cref="OperatingBoundaryValidationRelationQueryCapability"/>.</summary>
    public const string OperatingBoundaryValidationCapability = "operatingBoundaryValidation";

    /// <summary>Discriminator value for <see cref="PrimitiveRelationQueryCapability"/>.</summary>
    public const string PrimitiveCapability = "primitive";

    /// <summary>Discriminator property used by <see cref="RelationQueryRealizationDecision"/>.</summary>
    public const string DecisionDiscriminator = "$decision";

    /// <summary>Discriminator value for <see cref="NativeRelationQueryRealizationDecision"/>.</summary>
    public const string NativeDecision = "native";

    /// <summary>Discriminator value for <see cref="ComposedRelationQueryRealizationDecision"/>.</summary>
    public const string ComposedDecision = "composed";

    /// <summary>Discriminator value for <see cref="ConstrainedRelationQueryRealizationDecision"/>.</summary>
    public const string ConstrainedDecision = "constrained";

    /// <summary>Discriminator value for <see cref="OverrideRelationQueryRealizationDecision"/>.</summary>
    public const string OverrideDecision = "override";

    /// <summary>Discriminator value for <see cref="UnavailableRelationQueryRealizationDecision"/>.</summary>
    public const string UnavailableDecision = "unavailable";
}
