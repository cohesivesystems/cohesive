using System.Collections.Immutable;

namespace Cohesive.Adapters.Sql;

/// <summary>Allocates zero-based parameter slots in first-use order, deduplicating runtime binding identities.</summary>
/// <typeparam name="TSlot">Adapter-owned slot payload; use immutable payloads for immutable snapshots.</typeparam>
/// <remarks>
/// Mutable and single-threaded. Each constant occurrence gets a slot; repeated runtime bindings share one slot
/// using ordinal string equality. Adapters own payload normalization and marker syntax. Factories run synchronously
/// only for new slots and are never retained. A failed factory leaves allocation state unchanged.
/// </remarks>
public sealed class SqlParameterSlots<TSlot>
{
    readonly ImmutableArray<TSlot>.Builder slots = ImmutableArray.CreateBuilder<TSlot>();
    readonly Dictionary<string, int> runtimePositions = new(StringComparer.Ordinal);
    bool creatingSlot;

    /// <summary>Creates an empty allocator for one command construction.</summary>
    public SqlParameterSlots() { }

    /// <summary>Captures the current ordered slots without transferring the allocator's storage.</summary>
    /// <returns>An immutable shallow snapshot, unaffected by subsequent allocations.</returns>
    public ImmutableArray<TSlot> Snapshot() => slots.ToImmutable();

    /// <summary>Allocates a distinct slot for one constant occurrence.</summary>
    /// <param name="createSlot">Factory receiving the zero-based position; it must not reenter this allocator.</param>
    /// <returns>The new zero-based position.</returns>
    /// <exception cref="ArgumentNullException">The factory is null.</exception>
    /// <exception cref="InvalidOperationException">A factory reenters an allocation operation.</exception>
    /// <remarks>Factory exceptions propagate without allocating a slot.</remarks>
    public int AddConstant(Func<int, TSlot> createSlot)
    {
        ArgumentNullException.ThrowIfNull(createSlot);
        RequireNotCreatingSlot();
        var position = slots.Count;
        slots.Add(CreateSlot(createSlot, position));
        return position;
    }

    /// <summary>Returns the existing slot for a runtime binding, or allocates it on first use.</summary>
    /// <param name="binding">Nonempty, case-sensitive runtime binding identity.</param>
    /// <param name="createSlot">Factory receiving the zero-based position; invoked only on first use and must not reenter.</param>
    /// <returns>The binding's stable zero-based position within this allocator.</returns>
    /// <exception cref="ArgumentNullException">The binding or factory is null.</exception>
    /// <exception cref="ArgumentException">The binding is empty or white space.</exception>
    /// <exception cref="InvalidOperationException">A factory reenters an allocation operation.</exception>
    /// <remarks>Factory exceptions propagate without registering the binding or allocating a slot.</remarks>
    public int GetOrAddRuntime(string binding, Func<int, TSlot> createSlot)
    {
        Guard.RequireNotNullOrWhiteSpace(binding);
        ArgumentNullException.ThrowIfNull(createSlot);
        RequireNotCreatingSlot();
        if (runtimePositions.TryGetValue(binding, out var existing)) return existing;
        var position = slots.Count;
        var slot = CreateSlot(createSlot, position);
        runtimePositions.Add(binding, position);
        slots.Add(slot);
        return position;
    }

    TSlot CreateSlot(Func<int, TSlot> createSlot, int position)
    {
        creatingSlot = true;
        try
        {
            return createSlot(position);
        }
        finally
        {
            creatingSlot = false;
        }
    }

    void RequireNotCreatingSlot()
    {
        if (creatingSlot)
            throw new InvalidOperationException("A SQL parameter slot factory cannot reenter its allocator.");
    }
}
