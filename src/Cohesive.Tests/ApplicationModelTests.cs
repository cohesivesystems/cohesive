using Cohesive.Host.Composition;
using Microsoft.Extensions.DependencyInjection;
using Cohesive.Prelude;
using Cohesive.Execution;

namespace Cohesive.Tests;

public sealed class ApplicationModelTests
{
    public sealed class Dependency : IAuthoringModel
    {
        public bool Completed { get; private set; }
        public int CompletionCount { get; private set; }
        public void Complete() { Completed = true; CompletionCount++; }
    }
    public sealed class Consumer(Dependency dependency)
    {
        public Dependency Dependency { get; } = dependency;
        public bool PreparedAtConstruction { get; } = dependency.Completed;
    }
    public sealed class First : IExecutionDefinitionModel
    {
        public ExecutionDefinitionDocument Document { get; } = new InstanceConstantProcess("first").Definition.Document;
    }
    public sealed class Second : IExecutionDefinitionModel
    {
        public ExecutionDefinitionDocument Document { get; } = new InstanceConstantProcess("second").Definition.Document;
    }
    public sealed class CycleA(CycleB other) { public CycleB Other { get; } = other; }
    public sealed class CycleB(CycleA other) { public CycleA Other { get; } = other; }

    public sealed class Application(Dependency dependency, Consumer consumer) : ApplicationModel
    {
        public Dependency Dependency { get; } = dependency;
        public Consumer Consumer { get; } = consumer;
    }
    public interface ISettings { string Name { get; } }
    public sealed class Settings : ISettings { public string Name => "selected"; }
    public sealed class Configured(ISettings settings) { public ISettings Settings { get; } = settings; }
    public sealed class DisposableModel : IDisposable { public void Dispose() { } }
    public sealed class InvalidDependency : IAuthoringModel
    {
        public void Complete() => throw new InvalidOperationException("invalid declaration");
    }
    public sealed class MustNotConstruct
    {
        public MustNotConstruct(InvalidDependency dependency) => throw new Exception("Consumer must not run before invalid dependency fails.");
    }

    public sealed record FirstBindings(string Database);
    public sealed record SecondBindings(string Database);
    public sealed class NativeStorage : Cohesive.Storage.StorageModel
    {
        public NativeStorage()
        {
            First = Storage.Define(() => new FirstBindings("orders"));
            Second = Storage.Define(() => new SecondBindings("inventory"));
        }
        public FirstBindings First { get; }
        public SecondBindings Second { get; }
        public void AddLate() => Storage.Define(() => new FirstBindings("late"));
    }

    public abstract class InitApplicationBase : ApplicationModel
    {
        public required Dependency Dependency { get; init; }
    }
    public sealed class InitApplication : InitApplicationBase, IAuthoringModel
    {
        public required Consumer Consumer { get; init; }
        public required Dependency Alias { get; init; }
        public bool Completed { get; private set; }
        public void Complete()
        {
            Assert.True(Dependency.Completed);
            Assert.Same(Dependency, Consumer.Dependency);
            Assert.Same(Dependency, Alias);
            Completed = true;
        }
    }
    public sealed class InitCycle : ApplicationModel
    {
        public required RootConsumer Consumer { get; init; }
    }
    public sealed class RootConsumer(InitCycle root) { public InitCycle Root { get; } = root; }
    public sealed class InterfaceRoot : ApplicationModel
    {
        public required ISettings Settings { get; init; }
    }
    public sealed class MutableRoot : ApplicationModel
    {
        public required Dependency Dependency { get; set; }
    }
    public sealed class FieldRoot : ApplicationModel
    {
        public required Dependency Dependency;
    }
    public sealed class InvalidInitDependencyRoot : ApplicationModel
    {
        public required InvalidDependency Dependency { get; init; }
        public InvalidInitDependencyRoot() => throw new Exception("Root must not construct before dependency completion.");
    }

    [Fact]
    public void Init_polyfill_from_another_assembly_is_supported()
    {
        var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName("InitPolyfillFixture"),
            System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule("fixture");
        var marker = module.DefineType("System.Runtime.CompilerServices.IsExternalInit",
            System.Reflection.TypeAttributes.Public).CreateType()!;
        Assert.NotEqual(typeof(System.Runtime.CompilerServices.IsExternalInit), marker);
        var type = module.DefineType("PolyfillRoot", System.Reflection.TypeAttributes.Public, typeof(ApplicationModel));
        type.DefineDefaultConstructor(System.Reflection.MethodAttributes.Public);
        var field = type.DefineField("_dependency", typeof(Dependency), System.Reflection.FieldAttributes.Private);
        var property = type.DefineProperty("Dependency", System.Reflection.PropertyAttributes.None, typeof(Dependency), null);
        var getter = type.DefineMethod("get_Dependency", System.Reflection.MethodAttributes.Public |
            System.Reflection.MethodAttributes.SpecialName, typeof(Dependency), Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Ldfld, field);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        var setter = type.DefineMethod("set_Dependency", System.Reflection.MethodAttributes.Public |
            System.Reflection.MethodAttributes.SpecialName, System.Reflection.CallingConventions.HasThis,
            typeof(void), [marker], null, [typeof(Dependency)], null, null);
        il = setter.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        il.Emit(System.Reflection.Emit.OpCodes.Stfld, field);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);
        property.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(
            typeof(System.Runtime.CompilerServices.RequiredMemberAttribute).GetConstructor(Type.EmptyTypes)!, []));
        var rootType = type.CreateType()!;
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ApplicationModel), rootType);
        services.AddSingleton<Dependency>();
        var prepared = new ApplicationModelBuilder(services).Build();
        Assert.Same(prepared.Get<Dependency>(), rootType.GetProperty("Dependency")!.GetValue(prepared.Get<ApplicationModel>()));
    }

    public sealed class RequiredNonRoot
    {
        public required Dependency Dependency { get; init; }
    }
    public sealed class MixedRoot(Dependency dependency) : ApplicationModel
    {
        public Dependency ConstructorDependency { get; } = dependency;
        public required Dependency Dependency { get; init; }
    }
    public sealed class EarlyReadRoot : ApplicationModel
    {
        public required Dependency Dependency { get; init; }
        public EarlyReadRoot() => _ = Dependency!.Completed;
    }

    [Fact]
    public void Non_root_required_members_fail_before_automatic_activation()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<RequiredNonRoot>());
        Assert.Contains("RequiredNonRoot", error.Message);
        Assert.Contains("Dependency", error.Message);
        Assert.Contains("constructor injection", error.Message);
    }

    [Fact]
    public void Factory_cannot_hide_unsupported_required_members()
    {
        var builder = new ApplicationModelBuilder().Add(_ => new RequiredNonRoot { Dependency = null! });
        Assert.Contains("unsupported required members", Assert.Throws<InvalidOperationException>(() => builder.Build()).Message);
    }

    [Fact]
    public void Automatic_property_injection_rejects_mixed_constructor_dependencies()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<MixedRoot>());
        Assert.Contains("parameterless constructor", error.Message);
    }

    [Fact]
    public void Explicit_factory_owns_mixed_root_initialization()
    {
        var dependency = new Dependency();
        var root = ModelActivator.Create<MixedRoot>(services =>
            services.AddSingleton(_ => new MixedRoot(dependency) { Dependency = dependency }));
        Assert.Same(root.ConstructorDependency, root.Dependency);
    }

    [Fact]
    public void Early_property_read_reports_boundary_and_original_cause()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<EarlyReadRoot>());
        Assert.Contains("EarlyReadRoot", error.Message);
        Assert.Contains("before required init components were assigned", error.Message);
        Assert.IsType<NullReferenceException>(error.GetBaseException());
    }

    [Fact]
    public void Required_init_components_share_completed_dependencies_and_support_inherited_members()
    {
        var first = ModelActivator.Create<InitApplication>();
        var second = ModelActivator.Create<InitApplication>();
        Assert.True(first.Completed);
        Assert.True(first.Consumer.PreparedAtConstruction);
        Assert.Same(first.Dependency, first.Composition.Get<Dependency>());
        Assert.Equal(1, first.Dependency.CompletionCount);
        Assert.NotSame(first.Dependency, second.Dependency);
    }

    [Fact]
    public void Required_init_properties_resolve_explicit_interface_registrations()
    {
        var model = ModelActivator.Create<InterfaceRoot>(services => services.AddSingleton<ISettings, Settings>());
        Assert.Equal("selected", model.Settings.Name);
        Assert.Contains(nameof(ISettings), Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<InterfaceRoot>()).Message);
    }

    [Fact]
    public void Required_init_dependencies_preserve_cycle_detection()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<InitCycle>());
        Assert.Contains(nameof(InitCycle), error.Message);
        Assert.Contains(nameof(RootConsumer), error.Message);
    }

    [Fact]
    public void Required_init_dependency_failure_precedes_root_construction() =>
        Assert.Equal("invalid declaration", Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<InvalidInitDependencyRoot>()).Message);

    [Fact]
    public void Required_mutable_properties_and_fields_are_rejected()
    {
        Assert.Contains("init-only", Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<MutableRoot>()).Message);
        Assert.Contains("init-only", Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<FieldRoot>()).Message);
    }

    [Fact]
    public void Explicit_root_factory_cannot_publish_null_required_properties()
    {
        var builder = new ApplicationModelBuilder().Add<InterfaceRoot>(_ => new InterfaceRoot { Settings = null! });
        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("InterfaceRoot.Settings", error.Message);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => builder.Build()));
    }

    [Fact]
    public void Explicit_root_factory_retains_its_initialized_components()
    {
        var settings = new Settings();
        var root = ModelActivator.Create<InterfaceRoot>(services => services.AddSingleton(_ => new InterfaceRoot { Settings = settings }));
        Assert.Same(settings, root.Settings);
    }

    [Fact]
    public void Direct_builder_requires_property_dependencies_to_be_registered()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ApplicationModelBuilder().Add<InterfaceRoot>().Build());
        Assert.Contains("InterfaceRoot.Settings", error.Message);
        var builder = new ApplicationModelBuilder().Add<InitApplication>().Add<Consumer>().Add<Dependency>();
        Assert.True(builder.Build().Get<InitApplication>().Completed);
    }

    [Fact]
    public void Storage_model_owns_multiple_native_binding_types_in_declaration_order()
    {
        var model = ModelActivator.Create<NativeStorage>();
        Assert.Collection(model.Definition,
            value => Assert.Same(model.First, value),
            value => Assert.Same(model.Second, value));
        Assert.Equal(model.Definition, model.Definition);
        Assert.Throws<InvalidOperationException>(model.AddLate);
    }

    [Fact]
    public void Storage_builder_runs_each_recipe_once_and_rejects_edits_after_build()
    {
        var calls = 0;
        var builder = new Cohesive.Storage.StorageModelBuilder();
        var native = builder.Define(() => { calls++; return new FirstBindings("orders"); });
        Assert.Equal(1, calls);
        Assert.Same(native, Assert.Single(builder.Build()));
        Assert.Equal(builder.Build(), builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.Define(() => { calls++; return native; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Failed_storage_recipe_does_not_publish_a_contribution()
    {
        var builder = new Cohesive.Storage.StorageModelBuilder();
        var failure = new InvalidOperationException("invalid mapping");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => builder.Define<object>(() => throw failure)));
        Assert.Throws<InvalidOperationException>(() => builder.Define<object>(() => null!));
        Assert.Throws<ArgumentNullException>(() => builder.Define<object>(null!));
        Assert.Empty(builder.Build());
    }

    [Fact]
    public void Activation_infers_shared_dependencies_and_prepares_them_before_constructor_consumption()
    {
        var first = ModelActivator.Create<Application>();
        var second = ModelActivator.Create<Application>();
        Assert.Same(first.Dependency, first.Consumer.Dependency);
        Assert.True(first.Consumer.PreparedAtConstruction);
        Assert.Same(first.Dependency, first.Composition.Get<Dependency>());
        Assert.Equal(1, first.Dependency.CompletionCount);
        Assert.NotSame(first.Dependency, second.Dependency);
    }

    [Fact]
    public void Standard_DI_registration_selects_an_interface_implementation() =>
        Assert.Equal("selected", ModelActivator.Create<Configured>(services => services.AddSingleton<ISettings, Settings>()).Settings.Name);

    [Fact]
    public void Activation_rejects_missing_interface_registration() =>
        Assert.Contains(nameof(ISettings), Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<Configured>()).Message);

    [Fact]
    public void Activation_reports_cycle_path()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<CycleA>());
        Assert.Contains(nameof(CycleA), error.Message);
        Assert.Contains(nameof(CycleB), error.Message);
    }

    [Fact]
    public void Invalid_dependency_fails_before_consumer_constructor() =>
        Assert.Equal("invalid declaration", Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<MustNotConstruct>()).Message);

    [Fact]
    public void Runtime_resource_and_scoped_registration_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<DisposableModel>());
        Assert.Throws<ArgumentException>(() => ModelActivator.Create<Configured>(services => services.AddScoped<ISettings, Settings>()));
    }

    [Fact]
    public void Constructor_order_is_resolved_and_authoring_completed_once()
    {
        var builder = new ApplicationModelBuilder().Add<Consumer>().Add<Dependency>();
        var model = builder.Build();
        Assert.Same(model.Get<Dependency>(), model.Get<Consumer>().Dependency);
        Assert.True(model.Get<Dependency>().Completed);
        Assert.True(model.Get<Consumer>().PreparedAtConstruction);
        Assert.Same(model, builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.Add<First>());
    }

    [Fact]
    public void Conflicting_process_revision_fails_composition_with_canonical_diagnostics()
    {
        var builder = new ApplicationModelBuilder().Add<First>().Add<Second>();
        var error = Assert.Throws<ApplicationModelPreparationException>(() => builder.Build());
        Assert.Contains(error.Validation.Diagnostics, d => d.Code == ExecutionDefinitionDiagnosticCodes.CatalogRevisionDuplicate);
        Assert.Same(error, Assert.Throws<ApplicationModelPreparationException>(() => builder.Build()));
    }

    [Fact]
    public void Missing_dependency_fails_at_setup() => Assert.Throws<InvalidOperationException>(
        () => new ApplicationModelBuilder().Add<Consumer>().Build());

    [Fact]
    public void Cyclic_dependency_fails_at_setup() => Assert.Throws<InvalidOperationException>(
        () => new ApplicationModelBuilder().Add<CycleA>().Add<CycleB>().Build());

    [Fact]
    public void Failed_preparation_closes_authoring_and_caches_failure()
    {
        var count = 0;
        var state = new AuthoringState<object>(() => { count++; throw new ArgumentException("invalid"); });
        var error = Assert.Throws<ArgumentException>(() => state.Value);
        Assert.Same(error, Assert.Throws<ArgumentException>(() => state.Value));
        Assert.Equal(1, count);
        Assert.Throws<InvalidOperationException>(() => state.Edit(() => 0));
    }

    [Fact]
    public void Concurrent_freezing_prepares_once()
    {
        var count = 0;
        var state = new AuthoringState<object>(() => { Interlocked.Increment(ref count); return new(); });
        var results = new object[64];
        Parallel.For(0, results.Length, i => results[i] = state.Value);
        Assert.All(results, value => Assert.Same(results[0], value));
        Assert.Equal(1, count);
    }
}
