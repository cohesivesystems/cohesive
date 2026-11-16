using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using Cohesive.Host.Services;
using Cohesive.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Model;

public sealed class InputValidatorTests
{
    public sealed record Request([property: Required, MaxLength(64)] string Id);
    public sealed record Nested(Request Child);
    public sealed record Invalid([property: Range(1, 10)] int Value);
    public sealed class MutableInput { [MaxLength(2)] public string Id { get; set; } = "ok"; }
    sealed class OneCharacter : IClrShapeMetadataProvider
    {
        public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context) =>
            context.Target == ClrShapeMetadataTarget.Field && context.Property?.Name == "Id"
                ? new() { Constraints = [new MaxLengthConstraint(1)] } : ClrShapeMetadata.Empty;
    }

    [Theory]
    [InlineData(null, false)] [InlineData("", false)] [InlineData(" ", false)]
    [InlineData("\t\r\n", false)] [InlineData("\u2003", false)] [InlineData("ok", true)]
    public void SharedAndTypedValidationPreserveRequiredSemantics(string? id, bool expected)
    {
        var inputs = InputValidator.Create(o => o.UseDataAnnotations());
        var request = new Request(id!);
        Assert.Equal(expected, inputs.Validate(request).IsValid);
        Assert.Equal(expected, inputs.For<Request>().Validate(request).IsValid);
        Assert.False(inputs.Validate<Request>(null!).IsValid);
    }

    [Fact]
    public void LengthIsUtf16AndResultsAreNeverReused()
    {
        var inputs = InputValidator.Create(o => o.UseDataAnnotations());
        Assert.True(inputs.Validate(new Request(string.Concat(Enumerable.Repeat("😀", 32)))).IsValid);
        Assert.False(inputs.Validate(new Request(string.Concat(Enumerable.Repeat("😀", 32)) + "x")).IsValid);
        var request = new MutableInput();
        Assert.True(inputs.Validate(request).IsValid);
        request.Id = "long";
        Assert.False(inputs.Validate(request).IsValid);
        request.Id = "ok";
        Assert.True(inputs.Validate(request).IsValid);
        var weak = ValidateTemporary(inputs);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ValidateTemporary(InputValidator inputs)
    {
        var request = new MutableInput();
        Assert.True(inputs.Validate(request).IsValid);
        return new WeakReference(request);
    }

    [Fact]
    public async Task ConcurrentFirstUsePreparesOnlyOneValidator()
    {
        var preparations = 0;
        var inputs = InputValidator.Create(o => o.UseDataAnnotations().Configure(_ => Interlocked.Increment(ref preparations)));
        var all = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => inputs.For<Request>())));
        Assert.All(all, item => Assert.Same(all[0], item));
        Assert.Equal(1, preparations);
    }

    [Fact]
    public void ConfigurationIsIsolatedAndTypeSpecific()
    {
        var narrow = InputValidator.Create(o => o.UseDataAnnotations().For<Request>(b => b.AddMetadataProvider(new OneCharacter())));
        var normal = InputValidator.Create(o => o.UseDataAnnotations());
        Assert.False(narrow.Validate(new Request("ab")).IsValid);
        Assert.True(normal.Validate(new Request("ab")).IsValid);
        Assert.True(narrow.Validate(new Nested(new Request("ab"))).IsValid); // policy is per root, not global
        Assert.NotSame(narrow.For<Request>(), normal.For<Request>());
        InputValidationOptions retained = null!;
        var snapshot = InputValidator.Create(o => retained = o);
        retained.UseDataAnnotations().Configure(_ => throw new InvalidOperationException("late mutation"));
        Assert.True(snapshot.Validate(new Request(new string('x', 65))).IsValid);
    }

    [Fact]
    public void EagerRegistrationFailsBeforeRequestsAndLazyErrorsAreStable()
    {
        Assert.Throws<NotSupportedException>(() => InputValidator.Create(o => o.UseDataAnnotations().For<Invalid>()));
        var preparations = 0;
        var inputs = InputValidator.Create(o => o.UseDataAnnotations().Configure(_ => preparations++));
        Assert.Throws<NotSupportedException>(() => inputs.For<Invalid>());
        Assert.Throws<NotSupportedException>(() => inputs.For<Invalid>());
        Assert.Equal(1, preparations);
        Assert.Throws<ArgumentException>(() => InputValidator.Create(o => o.For<Request>().For<Request>()));
    }

    [Fact]
    public void HostingSharesConfigurationWithTypedInjectionAndPreparesAtRegistration()
    {
        var preparations = 0;
        var services = new ServiceCollection();
        services.AddCohesiveInputValidation(o => o.UseDataAnnotations().Configure(_ => preparations++).For<Request>());
        Assert.Equal(1, preparations);
        Assert.Throws<InvalidOperationException>(() => services.AddCohesiveInputValidation());
        using var provider = services.BuildServiceProvider();
        var shared = provider.GetRequiredService<IInputValidator>();
        var typed = provider.GetRequiredService<IInputValidator<Request>>();
        Assert.True(shared.Validate(new Request("ok")).IsValid);
        Assert.False(typed.Validate(new Request(" ")).IsValid);
        Assert.Equal(1, preparations);
    }

    [Fact]
    public void WarmCacheLookupAndPortableAdmissionAllocateNothing()
    {
        var inputs = InputValidator.Create(o => o.UseDataAnnotations());
        var typed = inputs.For<Request>();
        var value = ObservationValue.FromObject(new Request("ok"));
        for (var i = 0; i < 2000; i++) inputs.For<Request>().ValidatePortable(value);
        var minimum = long.MaxValue;
        for (var sample = 0; sample < 3; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) inputs.For<Request>().ValidatePortable(value);
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.Equal(0, minimum);
        Assert.Same(typed, inputs.For<Request>());
    }
}
