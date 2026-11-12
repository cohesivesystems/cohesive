using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cohesive.Model;

namespace Cohesive.Tests.Model;

public sealed class WeakPreparationCacheTests
{
    [Fact]
    public async Task FailedPreparationRetriesWithoutEvictingConcurrentSuccess()
    {
        WeakPreparationCache<object, object> cache = new();
        object key = new(), expected = new();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        // The factory blocks deliberately; give it a dedicated thread so suite load cannot starve the rendezvous.
        var failed = Task.Factory.StartNew(() => Assert.Throws<InvalidOperationException>(() => cache.Get(key, _ =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
            throw new InvalidOperationException("retry");
        })), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var calls = 0;
        var successful = Task.Run(() => cache.Get(key, _ => { Interlocked.Increment(ref calls); return expected; }));
        release.Set();
        await failed;
        Assert.Same(expected, await successful);
        var values = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            cache.Get(key, _ => throw new InvalidOperationException("A success must remain published.")))));
        Assert.All(values, value => Assert.Same(expected, value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void PreparationDoesNotRootCollectibleClrMetadata()
    {
        var reference = PrepareCollectibleType();
        for (var attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(reference.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference PrepareCollectibleType()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CollectibleCacheFixture"), AssemblyBuilderAccess.RunAndCollect);
        var builder = assembly.DefineDynamicModule("fixture").DefineType("Value", TypeAttributes.Public);
        var property = builder.DefineProperty("Number", PropertyAttributes.None, typeof(long), null);
        var getter = builder.DefineMethod("get_Number", MethodAttributes.Public | MethodAttributes.SpecialName, typeof(long), Type.EmptyTypes);
        getter.GetILGenerator().Emit(OpCodes.Ldc_I8, 0L);
        getter.GetILGenerator().Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        var type = builder.CreateType()!;
        _ = ShapeTypeInspector.GetReadableProperties(type);
        _ = ShapeTypeInspector.GetReadablePropertyMetadata(type);
        _ = new Cohesive.Model.Authoring.DefaultClrTypeRefMapper().Map(type, null);
        return new WeakReference(type);
    }
}
