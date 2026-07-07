namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Convenience constructors for semantic domain types.
/// </summary>
public static class DomainTypes
{
    public static TypeRef String() => new ScalarTypeRef(kind: ScalarTypeKind.String);
    
    public static TypeRef Int32() => new ScalarTypeRef(kind: ScalarTypeKind.Int32);
    
    public static TypeRef Decimal() => new ScalarTypeRef(kind: ScalarTypeKind.Decimal);
    
    public static TypeRef Bool() => new ScalarTypeRef(kind: ScalarTypeKind.Bool);
    
    public static TypeRef Guid() => new ScalarTypeRef(kind: ScalarTypeKind.Guid);
    
    public static TypeRef DateTime() => new ScalarTypeRef(kind: ScalarTypeKind.DateTime);
    
    public static TypeRef Enum(string name, params string[] members) => new EnumTypeRef(name: name, members: [.. members]);
    
    public static TypeRef EntityRef(string entity) => new EntityReferenceTypeRef(Entity: new(value: entity));
    
    public static TypeRef Array(TypeRef elementType) => new ArrayTypeRef(ElementType: elementType);
    
    public static TypeRef Object(params ObjectFieldTypeDef[] fields) => new ObjectTypeRef(fields: [.. fields]);
    
    public static TypeRef Quantity(string quantity, ScalarTypeKind baseKind = ScalarTypeKind.Decimal) =>
        new QuantityTypeRef(quantity: quantity, baseKind: baseKind);

    public static TypeRef Json(JsonTypeKind kind = JsonTypeKind.Any) => new JsonTypeRef(kind);
}
