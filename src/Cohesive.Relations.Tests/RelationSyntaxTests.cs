namespace Cohesive.Relations.Tests;

public sealed class RelationSyntaxTests
{
    [Fact]
    public async Task Select_MapsBasicDto()
    {
        var relation = Relation<CarrierDto>
            .From<Carrier>()
            .Select(c => new()
            {
                Id = c.Id,
                Name = c.LegalName,
                Mc = c.McNumber
            });

        var carrier = new Carrier
        {
            Id = Guid.NewGuid(),
            LegalName = "Atlas Transport",
            McNumber = "MC-12345",
            ParentNetworkId = Guid.NewGuid(),
            SafetyScore = 87
        };

        var inputs = new[] { ToObservation(carrier) };
        var outputs = await new RelationExecutor().ExecuteAsync(relation, inputs);
        var projected = ToObject<CarrierDto>(Assert.Single(outputs));

        Assert.Equal(carrier.Id, projected.Id);
        Assert.Equal(carrier.LegalName, projected.Name);
        Assert.Equal(carrier.McNumber, projected.Mc);
    }

    [Fact]
    public async Task MapFields_WithRename_MapsBasicDto()
    {
        RelationDefinition relation = Relation<CarrierDto>
            .From<Carrier>()
            .MapFields()
                .Rename(c => c.LegalName, d => d.Name)
                .Rename(c => c.McNumber, d => d.Mc);

        var carrier = new Carrier
        {
            Id = Guid.NewGuid(),
            LegalName = "Roadline Logistics",
            McNumber = "MC-7788",
            ParentNetworkId = Guid.NewGuid(),
            SafetyScore = 90
        };

        var outputs = await new RelationExecutor().ExecuteAsync(relation, [ToObservation(carrier)]);
        var projected = ToObject<CarrierDto>(Assert.Single(outputs));

        Assert.Equal(carrier.Id, projected.Id);
        Assert.Equal(carrier.LegalName, projected.Name);
        Assert.Equal(carrier.McNumber, projected.Mc);
    }

    [Fact]
    public async Task Select_ProjectsCollectionItems()
    {
        var relation = Relation<OrderSummaryDto>
            .From<OrderWithLines>()
            .Select(order => new OrderSummaryDto
            {
                OrderNumber = order.OrderNumber,
                Lines = order.Lines
                    .Select(line => new OrderLineSummaryDto
                    {
                        Sku = line.Sku,
                        Quantity = line.Quantity
                    })
                    .ToArray()
            });

        var order = new OrderWithLines
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORD-1",
            Lines =
            [
                new() { Sku = "SKU-1", Quantity = 2 },
                new() { Sku = "SKU-2", Quantity = 5 }
            ]
        };

        var outputs = await new RelationExecutor().ExecuteAsync(relation, [ToObservation(order)]);
        var projected = ToObject<OrderSummaryDto>(Assert.Single(outputs));

        Assert.Equal(order.OrderNumber, projected.OrderNumber);
        Assert.Equal(["SKU-1", "SKU-2"], projected.Lines.Select(x => x.Sku));
        Assert.Equal([2, 5], projected.Lines.Select(x => x.Quantity));
    }

    [Fact]
    public async Task Join_Select_ProjectsLoadSearchDocument()
    {
        var relation = Relation<LoadSearchDocument>
            .From<Load>()
            .Join<Carrier>(static (l, c) => l.CarrierId == c.Id)
            .Select(static (l, c) => new LoadSearchDocument
            {
                LoadId = l.Id,
                CarrierName = c.LegalName,
                Amount = l.TotalAmount,
                CarrierSafetyScore = c.SafetyScore
            });

        var carrier = new Carrier
        {
            Id = Guid.NewGuid(),
            LegalName = "Blue Ridge Carrier",
            McNumber = "MC-2001",
            ParentNetworkId = Guid.NewGuid(),
            SafetyScore = 72
        };
        var matchedLoad = new Load
        {
            Id = Guid.NewGuid(),
            CarrierId = carrier.Id,
            TotalAmount = 1850.75m,
            Status = "Booked"
        };
        var unmatchedLoad = new Load
        {
            Id = Guid.NewGuid(),
            CarrierId = Guid.NewGuid(),
            TotalAmount = 99m,
            Status = "Booked"
        };

        var outputs = await new RelationExecutor().ExecuteAsync(
            relation,
            [
                ToObservation(matchedLoad),
                ToObservation(unmatchedLoad),
                ToObservation(carrier)
            ]);

        var projected = ToObject<LoadSearchDocument>(Assert.Single(outputs));
        Assert.Equal(matchedLoad.Id, projected.LoadId);
        Assert.Equal(carrier.LegalName, projected.CarrierName);
        Assert.Equal(matchedLoad.TotalAmount, projected.Amount);
        Assert.Equal(carrier.SafetyScore, projected.CarrierSafetyScore);
    }

    [Fact]
    public async Task GroupBy_Derive_And_ChainedJoin_WorkEndToEnd()
    {
        var carrierA = new Carrier
        {
            Id = Guid.NewGuid(),
            LegalName = "North Star Freight",
            McNumber = "MC-1010",
            ParentNetworkId = Guid.NewGuid(),
            SafetyScore = 81
        };
        var carrierB = new Carrier
        {
            Id = Guid.NewGuid(),
            LegalName = "Prairie Haul",
            McNumber = "MC-2020",
            ParentNetworkId = carrierA.ParentNetworkId,
            SafetyScore = 68
        };

        var invoices = new[]
        {
            new Invoice { Id = Guid.NewGuid(), CarrierId = carrierA.Id, Amount = 700_000m },
            new Invoice { Id = Guid.NewGuid(), CarrierId = carrierA.Id, Amount = 450_000m },
            new Invoice { Id = Guid.NewGuid(), CarrierId = carrierB.Id, Amount = 125_000m }
        };

        var revenueRelation = Relation<CarrierRevenue>
            .From<Invoice>()
            .GroupBy(i => i.CarrierId)
            .Select(g => new CarrierRevenue
            {
                CarrierId = g.Key,
                TotalRevenue = g.Sum(i => i.Amount)
            });

        var invoiceInputs = invoices.Select(ToObservation).ToArray();
        var revenueOutputs = await new RelationExecutor().ExecuteAsync(revenueRelation, invoiceInputs);
        Assert.Equal(2, revenueOutputs.Count);
        var revenues = revenueOutputs
            .Select(ToObject<CarrierRevenue>)
            .OrderBy(x => x.TotalRevenue)
            .ToArray();
        Assert.Equal(125_000m, revenues[0].TotalRevenue);
        Assert.Equal(1_150_000m, revenues[1].TotalRevenue);

        var riskRelation = Relation<CarrierRisk>
            .From<CarrierRevenue>()
            .Select(r => new CarrierRisk
            {
                CarrierId = r.CarrierId,
                RiskTier =
                    r.TotalRevenue > 1_000_000m ? "High"
                    : r.TotalRevenue > 250_000m ? "Medium"
                    : "Low"
            });

        var riskOutputs = await new RelationExecutor().ExecuteAsync(riskRelation, revenueOutputs);
        Assert.Equal(2, riskOutputs.Count);
        var carrierRisks = riskOutputs
            .Select(ToObject<CarrierRisk>)
            .OrderBy(x => x.RiskTier, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal("High", carrierRisks[0].RiskTier);
        Assert.Equal("Low", carrierRisks[1].RiskTier);

        var networkRiskRelation = Relation<NetworkRisk>
            .From<CarrierRisk>()
            .Join<Carrier>((r, c) => r.CarrierId == c.Id)
            .Select((r, c) => new NetworkRisk
            {
                NetworkId = c.ParentNetworkId,
                RiskTier = r.RiskTier
            });

        var networkInputs = riskOutputs
            .Concat([ToObservation(carrierA), ToObservation(carrierB)])
            .ToArray();
        var networkOutputs = await new RelationExecutor().ExecuteAsync(networkRiskRelation, networkInputs);
        Assert.Equal(2, networkOutputs.Count);

        var risks = networkOutputs
            .Select(ToObject<NetworkRisk>)
            .OrderBy(x => x.RiskTier, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(risks, x => x.RiskTier == "High");
        Assert.Contains(risks, x => x.RiskTier == "Low");
    }

    static Observation ToObservation<T>(T source)
    {
        return ObjectObservationMapper
            .For<T>(new(typeof(T).Name))
            .MapAll()
            .Build()
            .Map(source!);
    }

    static T ToObject<T>(Observation observation)
    {
        return ObservationObjectMapper
            .For<T>(observation.Layout)
            .MapAllFromJsonPropertyName()
            .Build()
            .Map(observation);
    }

    sealed record Carrier
    {
        public Guid Id { get; init; }
        public string LegalName { get; init; } = string.Empty;
        public string McNumber { get; init; } = string.Empty;
        public int SafetyScore { get; init; }
        public Guid ParentNetworkId { get; init; }
    }

    sealed record CarrierDto
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Mc { get; init; } = string.Empty;
    }

    sealed record Load
    {
        public Guid Id { get; init; }
        public Guid CarrierId { get; init; }
        public decimal TotalAmount { get; init; }
        public string Status { get; init; } = string.Empty;
    }

    sealed record LoadSearchDocument
    {
        public Guid LoadId { get; init; }
        public string CarrierName { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public int CarrierSafetyScore { get; init; }
    }

    sealed record OrderWithLines
    {
        public Guid Id { get; init; }
        public string OrderNumber { get; init; } = string.Empty;
        public IReadOnlyList<OrderLine> Lines { get; init; } = [];
    }

    sealed record OrderLine
    {
        public string Sku { get; init; } = string.Empty;
        public int Quantity { get; init; }
    }

    sealed record OrderSummaryDto
    {
        public string OrderNumber { get; init; } = string.Empty;
        public IReadOnlyList<OrderLineSummaryDto> Lines { get; init; } = [];
    }

    sealed record OrderLineSummaryDto
    {
        public string Sku { get; init; } = string.Empty;
        public int Quantity { get; init; }
    }

    sealed record Invoice
    {
        public Guid Id { get; init; }
        public Guid CarrierId { get; init; }
        public decimal Amount { get; init; }
    }

    sealed record CarrierRevenue
    {
        public Guid CarrierId { get; init; }
        public decimal TotalRevenue { get; init; }
    }

    sealed record CarrierRisk
    {
        public Guid CarrierId { get; init; }
        public string RiskTier { get; init; } = string.Empty;
    }

    sealed record NetworkRisk
    {
        public Guid NetworkId { get; init; }
        public string RiskTier { get; init; } = string.Empty;
    }
}
