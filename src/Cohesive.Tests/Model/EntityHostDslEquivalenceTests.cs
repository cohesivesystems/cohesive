using System.Text.Json.Nodes;
using Cohesive.Transitions.Model;
using Cohesive.Host.Configuration;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Model;

/// <summary>
/// Verifies that host-authored entities compile to the same semantic definition as direct declarative DSL.
/// </summary>
public sealed class EntityHostDslEquivalenceTests
{
    [Fact]
    public void HostDslEntityDefinition_IsEquivalentToDirectDslDefinition()
    {
        var hostEntity = new HostOrderEntity();
        var hostDefinition = hostEntity.Definition;
        var directDefinition = BuildDirectDslDefinition();

        var hostModel = new DomainModelDefinition([hostDefinition], version: "1");
        var directModel = new DomainModelDefinition([directDefinition], version: "1");

        var hostJson = DomainModelExternalDsl.ToJson(hostModel, indented: false);
        var directJson = DomainModelExternalDsl.ToJson(directModel, indented: false);
        var hostNode = JsonNode.Parse(hostJson);
        var directNode = JsonNode.Parse(directJson);

        Assert.True(
            JsonNode.DeepEquals(hostNode, directNode),
            $"Compiled host DSL definition differs from direct DSL definition.{Environment.NewLine}Host: {hostJson}{Environment.NewLine}Direct: {directJson}");
    }

    static EntityDefinition BuildDirectDslDefinition()
    {
        var model = DomainModelDsl.Define(domain => domain
            .Entity("Order", order => order
                .Annotation("entity.meta", new
                {
                    source = "object-annotation",
                    tags = new[] { "host", "direct" }
                })
                .Field("OrderId", DomainTypes.String(), f => f.WriteOnce())
                .Field("Status", DomainTypes.String())
                .Field("CarrierId", DomainTypes.String(), f => f.Optional())
                .Invariant(
                    "AssignedRequiresCarrier",
                    Expr.Or(
                        Expr.Ne(Expr.Field("Status"), Expr.Const("Assigned")),
                        Expr.And(
                            Expr.Ne(Expr.Field("CarrierId"), Expr.Null()),
                            Expr.Ne(Expr.Field("CarrierId"), Expr.Const("")))))
                .Transition(
                    "AssignCarrier",
                    t => t
                        .Parameter("CarrierId", DomainTypes.String(), isRequired: true)
                        .Requires(
                            "CanAssignCarrier",
                            Expr.And(
                                Expr.Eq(Expr.Field("Status"), Expr.Const("Draft")),
                                Expr.Ne(Expr.Param("CarrierId"), Expr.Const(""))))
                        .Set("CarrierId", Expr.Param("CarrierId"))
                        .Set("Status", Expr.Const("Assigned"))
                        .Emit(
                            "CarrierAssigned",
                            Expr.Call(
                                "object",
                                Expr.Const("orderId"),
                                Expr.Field("OrderId"),
                                Expr.Const("carrierId"),
                                Expr.Param("CarrierId"))))));

        return Assert.Single(model.Entities);
    }

    sealed class HostOrderEntity : Entity
    {
        static readonly FieldDefinition OrderIdDef = FieldDefinition.Create(
            new FieldName(nameof(OrderId)),
            DomainTypes.String(),
            mutability: FieldMutability.WriteOnce);

        static readonly FieldDefinition StatusDef = FieldDefinition.Create(
            new FieldName(nameof(Status)),
            DomainTypes.String());

        static readonly FieldDefinition CarrierIdDef = FieldDefinition.Create(
            new FieldName(nameof(CarrierId)),
            DomainTypes.String(),
            presence: FieldPresence.Optional);

        public HostOrderEntity() : base("Order")
        {
            Annotate("entity.meta", new
            {
                source = "object-annotation",
                tags = new[] { "host", "direct" }
            });

            OrderId = Field<string>(OrderIdDef);
            Status = Field(StatusDef, "Draft");
            CarrierId = Field<string?>(CarrierIdDef, initialValue: null);

            Invariant<HostOrderEntity>(
                "AssignedRequiresCarrier",
                e => e.Status != "Assigned" || (e.CarrierId != null && e.CarrierId != ""));

            AssignCarrier = Transition<HostOrderEntity, AssignCarrierInput>(
                "AssignCarrier",
                t => t
                    .Requires("CanAssignCarrier", (e, p) => e.Status == "Draft" && p.CarrierId != "")
                    .Set(e => e.CarrierId, (_, p) => p.CarrierId)
                    .Set(e => e.Status, (_, _) => "Assigned")
                    .Emit("CarrierAssigned", (e, p) => new { orderId = e.OrderId, carrierId = p.CarrierId }));

        }

        public Field<string> OrderId { get; }

        public Field<string> Status { get; }

        public Field<string?> CarrierId { get; }

        public Transition<HostOrderEntity, AssignCarrierInput> AssignCarrier { get; }

        public sealed record AssignCarrierInput(string CarrierId);
    }
}
