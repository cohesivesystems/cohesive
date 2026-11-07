using System.Collections.Immutable;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Machines;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;
using Cohesive.Transitions.Model;

internal sealed class LoadApplication
{
    internal static string ExampleDirectory
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "LoadActions.csproj"))) return directory.FullName;
            throw new InvalidOperationException("Run this source example from its build output.");
        }
    }
    internal const string Organization = "org-a";
    internal const string RoleHeader = "X-Demo-Role";
    internal const string TokenHeader = "X-Expected-Concurrency-Token";
    internal static readonly (string Id, bool DocumentsComplete)[] Loads = [("LD-2041", true), ("LD-2042", false)];
    internal static readonly string[] Roles = ["dispatcher", "supervisor", "viewer"];
    internal EntityDefinition Entity { get; }
    internal InMemoryEntityOutboxRepository Repository { get; }
    internal ServiceRuntime Runtime { get; }
    internal ApiDefinition Commands { get; }
    internal ApiEndpoint Inspection { get; }
    internal ApiEndpoint Details { get; }
    internal ApiEndpoint Catalog { get; }
    internal string InspectionRoute => "/api/loads/{id}/actions";

    internal LoadApplication()
    {
        var author = new EntityTransitionAuthoring<Load, Command>(new("load"));
        Entity = author.Entity;
        var source = Machine.Define("load.lifecycle", "1");
        var ready = source.State("ready").Initial();
        var released = source.State("released").Final();
        var cancelled = source.State("cancelled").Final();
        var release = source.Edge("release").From(ready).To(released)
            .When(author.Guard((load, command) => load.DocumentsComplete));
        var cancel = source.Edge("cancel").From(ready).To(cancelled);
        var links = author.Discriminator(source.Build(), load => load.Status)
            .Map(ready, LoadStatus.Ready).Map(released, LoadStatus.Released).Map(cancelled, LoadStatus.Cancelled).Build();
        CompiledTransitionPlan Plan(MachineMovement movement)
        {
            var metadata = new TransitionAuthoringMetadata(new("load." + movement.Id), new("1"), new("body"),
                new(new(TransitionAuthoring.Producer), new("examples/load-actions"), DocumentOrigin.User));
            var transition = author.Create<string>(metadata, t => t.Move(movement, (load, command) => "Action unavailable")
                .Set(new("record-reason"), load => load.LastReason, (load, command) => command.Reason)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Updated"));
            var compiled = TransitionStaticCompiler.Compile(transition.Document, machineLinks: links);
            return compiled.Plan ?? throw new InvalidOperationException(string.Join("; ", compiled.Validation.Diagnostics.Select(d => d.Message)));
        }
        var plans = new[] { (Movement: release, Plan: Plan(release)), (Movement: cancel, Plan: Plan(cancel)) };
        var declaration = new ServiceDefinition([.. plans.Select(item => new ServiceTransitionOperation(item.Movement.Id,
            Entity.StateShape.QualifiedId, item.Plan.DefinitionReference, [new("loads." + item.Movement.Id)]))]);
        var service = ServiceDefinitionDocuments.Create(new("loads"), new("1"), declaration,
            new(new("examples"), new("load-actions"), DocumentOrigin.User));
        Repository = new(Entity, _ => Organization);
        Runtime = new(service, plans.Select(item => new ServiceTransitionBinding(item.Movement.Id, item.Plan, Entity, _ => Repository)),
            new IdentityServiceInvocationAuthorization("organization", new(nameof(Load.OrganizationId))));
        Commands = new ApiDefinition(plans.Select(item => Runtime.Project<Command, string>(item.Movement.Id,
            new("POST", "/api/loads/{id}/" + item.Movement.Id,
                [new("id", HttpParameterSource.Route, typeof(string)), new(TokenHeader, HttpParameterSource.Header, typeof(string))],
                new(typeof(Command)))).Operation).ToArray());
        Inspection = Runtime.ProjectTransitionInspection<Command>(Entity.StateShape.QualifiedId,
            new("POST", InspectionRoute, [new("id", HttpParameterSource.Route, typeof(string))], new(typeof(ServiceTransitionInspection<Command>))));
        Details = Cohesive.Api.Api.Define("LoadOperations").Entity<Load>()
            .Query("Details").Route("GET", "/api/loads/{id}").RouteParameter<string>("id")
            .Returns<LoadDetails>().Result(ApiResultKind.NotFound).Build();
        var reads = Cohesive.Api.Api.Define("LoadOperations")
            .Action("Catalog").Route("GET", "/api/catalog").Returns<LoadCatalog>().Done()
            .Build();
        Catalog = reads.Endpoints.Single(endpoint => endpoint.Operation.Name == "Catalog");
    }

    internal Task Seed(string id, bool documentsComplete) => Repository.Upsert(OperationContext.Create(),
        new(Entity.CreateState(id, new Load(Organization, LoadStatus.Ready, documentsComplete, "")).Snapshot));

    internal IdentityContext Identity(string role)
    {
        var capabilities = role switch
        {
            "dispatcher" => ImmutableArray.Create("loads.release"),
            "supervisor" => ImmutableArray.Create("loads.release", "loads.cancel"),
            "viewer" => ImmutableArray<string>.Empty,
            _ => throw new ArgumentException("Unknown demo role.", nameof(role))
        };
        var actor = new PrincipalRef("demo-operator", PrincipalKind.User);
        var scope = new ScopeRef(Organization, "organization");
        return new(actor, EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new IdentityScopeGrant(actor, scope, capabilities, "demo-role-resolver")]);
    }
}

/// <summary>Public load detail returned to the local demo.</summary>
/// <param name="Id">Load identity.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="DocumentsComplete">Whether release documents are ready.</param>
/// <param name="LastReason">Last accepted operator reason.</param>
public sealed record LoadDetails(string Id, LoadStatus Status, bool DocumentsComplete, string LastReason);
/// <summary>Host-declared local demo choices.</summary>
/// <param name="Roles">Available demo identities.</param>
/// <param name="Loads">Seeded load identities.</param>
/// <param name="RoleHeader">Header used by the local demo identity resolver.</param>
public sealed record LoadCatalog(IReadOnlyList<string> Roles, IReadOnlyList<string> Loads, string RoleHeader);
