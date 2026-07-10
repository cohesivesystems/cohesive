namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Convenience constructors for semantic domain types.
/// </summary>
public static class DomainTypes
{
    /// <summary>Creates a string type reference.</summary>
    public static TypeRef String() => new ScalarTypeRef(kind: ScalarTypeKind.String);
    
    /// <summary>Creates a 32-bit integer type reference.</summary>
    public static TypeRef Int32() => new ScalarTypeRef(kind: ScalarTypeKind.Int32);
    
    /// <summary>Creates a decimal type reference.</summary>
    public static TypeRef Decimal() => new ScalarTypeRef(kind: ScalarTypeKind.Decimal);
    
    /// <summary>Creates a Boolean type reference.</summary>
    public static TypeRef Bool() => new ScalarTypeRef(kind: ScalarTypeKind.Bool);
    
    /// <summary>Creates a GUID type reference.</summary>
    public static TypeRef Guid() => new ScalarTypeRef(kind: ScalarTypeKind.Guid);
    
    /// <summary>Creates a date-time type reference.</summary>
    public static TypeRef DateTime() => new ScalarTypeRef(kind: ScalarTypeKind.DateTime);
    
    /// <summary>Creates an enumeration type reference.</summary>
    public static TypeRef Enum(string name, params string[] members) => new EnumTypeRef(name: name, members: [.. members]);
    
    /// <summary>Creates an entity-reference type reference.</summary>
    public static TypeRef EntityRef(string entity) => new EntityReferenceTypeRef(Entity: new(value: entity));
    
    /// <summary>Creates an array type reference.</summary>
    public static TypeRef Array(TypeRef elementType) => new ArrayTypeRef(ElementType: elementType);
    
    /// <summary>Creates an object type reference.</summary>
    public static TypeRef Object(params ObjectFieldTypeDef[] fields) => new ObjectTypeRef(fields: [.. fields]);
    
    /// <summary>Creates a quantity type reference.</summary>
    public static TypeRef Quantity(string quantity, ScalarTypeKind baseKind = ScalarTypeKind.Decimal) =>
        new QuantityTypeRef(quantity: quantity, baseKind: baseKind);

    /// <summary>Creates a JSON type reference.</summary>
    public static TypeRef Json(JsonTypeKind kind = JsonTypeKind.Any) => new JsonTypeRef(kind);
}
