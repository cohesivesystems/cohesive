// Source-linked adapters isolate internal types without changing the preparation algorithm.
#if COHESIVE_POSTGRES_INTERNAL_PREPARATION
namespace Cohesive.Adapters.Postgres.Internal;
#else
namespace Cohesive.Model;
#endif

// One owner-scoped preparation publishes only completed factories. Exceptions leave this same
// slot retryable, so no removal can race with a later successful publication.
internal sealed class PreparationSlot<TValue>
{
    TValue value = default!;
    int prepared;
    internal bool IsPrepared => Volatile.Read(ref prepared) != 0;
    internal bool TryGet(out TValue result)
    {
        if (IsPrepared) { result = value; return true; }
        result = default!;
        return false;
    }
    internal TValue Get<TContext>(TContext context, Func<TContext, TValue> factory)
    {
        if (IsPrepared) return value;
        lock (this)
        {
            if (!IsPrepared)
            {
                value = factory(context);
                Volatile.Write(ref prepared, 1);
            }
            return value;
        }
    }
}
