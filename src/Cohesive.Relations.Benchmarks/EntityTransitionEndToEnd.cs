using System.Text;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Adapters.SQLite;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Physical store behind the benchmarked entity API endpoint.</summary>
public enum EntityTransitionStore
{
    /// <summary>The in-memory outbox repository; isolates framework work from storage encoding and I/O.</summary>
    InMemory,

    /// <summary>A file-backed SQLite repository; requests and stored rows cross real byte boundaries.</summary>
    Sqlite
}

/// <summary>How the endpoint projects the committed snapshot into the HTTP response.</summary>
public enum EntityTransitionResponse
{
    /// <summary>Conventional application code: read observation fields into a CLR resource, then serialize it.</summary>
    ClrResource,

    /// <summary>Write the committed observation value directly through its JSON converter.</summary>
    Observation
}

// One warm entity API transition from request bytes to response bytes through the real ASP.NET binding:
// body deserialization, repository read, reference decision, candidate preparation, commit and response writing.
// Requests alternate between two carriers so every invocation is an Applied, committed transition.
internal sealed class EntityTransitionEndToEnd : IDisposable
{
    const string Route = "/shipments/{id}/assign";
    const string ShipmentId = "shipment-1";
    const string Tenant = "tenant-a";

    readonly WebApplication app;
    readonly RequestDelegate endpoint;
    readonly byte[][] requests;
    readonly MemoryStream response = new(capacity: 4096);
    readonly string? databaseDirectory;
    int invocation;

    EntityTransitionEndToEnd(WebApplication app, RequestDelegate endpoint, byte[][] requests, string? databaseDirectory,
        IEntityRepository repository, CompiledTransitionPlan plan, OperationContext context)
    {
        this.app = app;
        this.endpoint = endpoint;
        this.requests = requests;
        this.databaseDirectory = databaseDirectory;
        Repository = repository;
        Plan = plan;
        Context = context;
    }

    internal IEntityRepository Repository { get; }
    internal CompiledTransitionPlan Plan { get; }
    internal OperationContext Context { get; }
    internal static EntityDefinition Entity => ShipmentEntity.Instance.Definition;

    internal static EntityTransitionEndToEnd Create(EntityTransitionStore store, EntityTransitionResponse responseKind)
    {
        var entity = ShipmentEntity.Instance;
        var context = OperationContext.Create(new FixedTimeProvider(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)));
        string? directory = null;
        IEntityRepository repository;
        if (store == EntityTransitionStore.Sqlite)
        {
            directory = Directory.CreateTempSubdirectory("cohesive-entity-transition-").FullName;
            var mapping = new SqliteEntityRepositoryMapping(entity.Definition,
                identityField: nameof(ShipmentState.Id), partitionField: nameof(ShipmentState.Tenant), tableName: "shipments");
            var database = new SqliteDatabase(new SqliteDatabaseOptions(Path.Combine(directory, "shipments.db")));
            new SqliteSchema("benchmarks/shipments", [mapping.InitialMigration]).Apply(database);
            repository = new SqliteEntityRepository(database, mapping);
        }
        else
            repository = new InMemoryEntityOutboxRepository(entity.Definition, partitionKeyFieldName: nameof(ShipmentState.Tenant));

        repository.Upsert(context, new(entity.Definition.CreateState(ShipmentId, InitialState(context.UtcNow)).Snapshot))
            .GetAwaiter().GetResult();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            options.SerializerOptions.DictionaryKeyPolicy = null;
        });
        builder.Services.AddSingleton(context);
        builder.Services.RegisterEntityRepository(entity.Definition, (_, _) => repository);
        var app = builder.Build();

        var plan = CompileAssign(entity);
        var api = Cohesive.Api.Api.Define()
            .Entity<ShipmentResource>()
                .Command("Assign")
                    .Route("POST", Route)
                    .RouteParameter<string>("id")
                    .Body<AssignShipmentRequest>()
                    .Returns<ShipmentResource>()
                    .Transition(plan.DefinitionReference)
                    .Done()
            .Build();
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult = responseKind == EntityTransitionResponse.ClrResource
            ? static (_, snapshot) => Results.Ok(ToResource(snapshot))
            : static (_, snapshot) => Results.Ok(snapshot.Entity.Observation.Value);
        app.MapEntityApiDefinition(api, new EntityApiEndpointOptions { Entity = entity.Definition }
            .Bind(EntityApiOperationBinding.Transition(
                "Assign",
                plan,
                static (context, request) => new ShipmentEntity.AssignInput(
                    ((AssignShipmentRequest)request!).Carrier, context.OperationContext.UtcNow),
                createResult)));

        var routeEndpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(static candidate => candidate.RoutePattern.RawText == Route);
        byte[][] requests =
        [
            JsonSerializer.SerializeToUtf8Bytes(new AssignShipmentRequest("carrier-north")),
            JsonSerializer.SerializeToUtf8Bytes(new AssignShipmentRequest("carrier-south"))
        ];
        return new(app, routeEndpoint.RequestDelegate!, requests, directory, repository, plan, context);
    }

    /// <summary>Runs one request through the mapped endpoint and returns the response byte count.</summary>
    internal async Task<int> InvokeAsync()
    {
        var body = requests[invocation++ & 1];
        response.SetLength(0);
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.Request.Method = "POST";
        http.Request.Path = "/shipments/shipment-1/assign";
        http.Request.RouteValues["id"] = ShipmentId;
        http.Request.ContentType = "application/json";
        http.Request.ContentLength = body.Length;
        http.Request.Body = new MemoryStream(body, writable: false);
        http.Response.Body = response;
        await endpoint(http).ConfigureAwait(false);
        if (http.Response.StatusCode != StatusCodes.Status200OK)
            throw new InvalidOperationException(
                $"Transition returned {http.Response.StatusCode}: {Encoding.UTF8.GetString(response.ToArray())}");
        return (int)response.Length;
    }

    internal string LastResponse => Encoding.UTF8.GetString(response.GetBuffer(), 0, (int)response.Length);

    /// <summary>Creates the alternating transition input exactly as the binding does for a given request.</summary>
    internal PortableValue NextInput() => PortableValue.Concrete(Plan.Definition.Input, ObservationValue.FromObject(
        new ShipmentEntity.AssignInput((invocation++ & 1) == 0 ? "carrier-north" : "carrier-south", Context.UtcNow)));

    public void Dispose()
    {
        ((IDisposable)app).Dispose();
        if (databaseDirectory is not null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    static ShipmentState InitialState(DateTimeOffset now) => new(
        Id: ShipmentId, Tenant: Tenant, Status: "Booked", Carrier: "unassigned", Origin: "Chicago, IL",
        Destination: "Dallas, TX", Reference: "PO-2026-000123", WeightKg: 1234.5600m, Pieces: 24, Hazmat: false,
        PickupAtUtc: now.AddDays(1), DeliverByUtc: now.AddDays(3), UpdatedAtUtc: now);

    static CompiledTransitionPlan CompileAssign(ShipmentEntity entity)
    {
        var authored = TransitionAuthoring.Create<ShipmentEntity, ShipmentEntity.AssignInput, bool>(
            entity.Definition.Shape,
            new(
                definitionId: new("benchmarks/shipments/assign"),
                revisionId: new("revision/1"),
                bodyId: new("assign/body"),
                provenance: new(new(TransitionAuthoring.Producer), new("benchmarks/entity-transition"), DocumentOrigin.Generated),
                displayName: "Assign carrier"),
            transition => transition
                .Requires(new("assign/not-delivered"), (shipment, _) => shipment.Status != "Delivered", (_, _) => false)
                .Set(new("assign/set-carrier"), shipment => shipment.Carrier, (_, input) => input.Carrier)
                .Set(new("assign/set-status"), shipment => shipment.Status, "Assigned")
                .Set(new("assign/set-updated-at"), shipment => shipment.UpdatedAtUtc, (_, input) => input.UpdatedAtUtc)
                .Return(new("assign/applied"), TransitionOutcomeDisposition.Applied, true));
        var compilation = authored.Compile();
        if (!compilation.IsSuccessful)
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                compilation.Validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        return (CompiledTransitionPlan)compilation.Plan!;
    }

    internal static ShipmentResource ToResource(EntitySnapshot snapshot)
    {
        var state = snapshot.Entity.Observation;
        return new(
            Id: state.GetField(nameof(ShipmentState.Id)).GetRequiredString(),
            Tenant: state.GetField(nameof(ShipmentState.Tenant)).GetRequiredString(),
            Status: state.GetField(nameof(ShipmentState.Status)).GetRequiredString(),
            Carrier: state.GetField(nameof(ShipmentState.Carrier)).GetRequiredString(),
            Origin: state.GetField(nameof(ShipmentState.Origin)).GetRequiredString(),
            Destination: state.GetField(nameof(ShipmentState.Destination)).GetRequiredString(),
            Reference: state.GetField(nameof(ShipmentState.Reference)).GetRequiredString(),
            WeightKg: state.GetField(nameof(ShipmentState.WeightKg)).GetDecimal(),
            Pieces: state.GetField(nameof(ShipmentState.Pieces)).GetInt64(),
            Hazmat: state.GetField(nameof(ShipmentState.Hazmat)).GetBoolean(),
            PickupAtUtc: state.GetField(nameof(ShipmentState.PickupAtUtc)).GetDateTimeOffset(),
            DeliverByUtc: state.GetField(nameof(ShipmentState.DeliverByUtc)).GetDateTimeOffset(),
            UpdatedAtUtc: state.GetField(nameof(ShipmentState.UpdatedAtUtc)).GetDateTimeOffset());
    }

    sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>Benchmark entity state with a representative mix of scalar kinds.</summary>
public sealed record ShipmentState(
    string Id, string Tenant, string Status, string Carrier, string Origin, string Destination, string Reference,
    decimal WeightKg, long Pieces, bool Hazmat, DateTimeOffset PickupAtUtc, DateTimeOffset DeliverByUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Public resource returned by the benchmark endpoint.</summary>
public sealed record ShipmentResource(
    string Id, string Tenant, string Status, string Carrier, string Origin, string Destination, string Reference,
    decimal WeightKg, long Pieces, bool Hazmat, DateTimeOffset PickupAtUtc, DateTimeOffset DeliverByUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Request body for the carrier assignment command.</summary>
public sealed record AssignShipmentRequest(string Carrier);

sealed class ShipmentEntity : Entity<ShipmentEntity>
{
    public sealed record AssignInput(string Carrier, DateTimeOffset UpdatedAtUtc);

    public ShipmentEntity()
    {
        Id = WriteOnceField<string>(nameof(Id));
        Tenant = WriteOnceField<string>(nameof(Tenant));
        Status = MutableField<string>(nameof(Status));
        Carrier = MutableField<string>(nameof(Carrier));
        Origin = MutableField<string>(nameof(Origin));
        Destination = MutableField<string>(nameof(Destination));
        Reference = MutableField<string>(nameof(Reference));
        WeightKg = MutableField<decimal>(nameof(WeightKg));
        Pieces = MutableField<long>(nameof(Pieces));
        Hazmat = MutableField<bool>(nameof(Hazmat));
        PickupAtUtc = MutableField<DateTimeOffset>(nameof(PickupAtUtc));
        DeliverByUtc = MutableField<DateTimeOffset>(nameof(DeliverByUtc));
        UpdatedAtUtc = MutableField<DateTimeOffset>(nameof(UpdatedAtUtc));
    }

    public Field<string> Id { get; }
    public Field<string> Tenant { get; }
    public Field<string> Status { get; }
    public Field<string> Carrier { get; }
    public Field<string> Origin { get; }
    public Field<string> Destination { get; }
    public Field<string> Reference { get; }
    public Field<decimal> WeightKg { get; }
    public Field<long> Pieces { get; }
    public Field<bool> Hazmat { get; }
    public Field<DateTimeOffset> PickupAtUtc { get; }
    public Field<DateTimeOffset> DeliverByUtc { get; }
    public Field<DateTimeOffset> UpdatedAtUtc { get; }
}
