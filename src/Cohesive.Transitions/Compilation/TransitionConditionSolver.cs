using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Cohesive.Transitions.Compilation;

/// <summary>
/// Identifies one canonical Boolean condition owned by a <see cref="TransitionConditionSolver"/>.
/// </summary>
/// <param name="Node">Solver-local reduced ordered binary decision diagram node.</param>
/// <remarks>
/// Conditions are meaningful only to the solver that produced them. The default value represents
/// <see langword="false"/> because terminal node zero is the false terminal in every solver.
/// </remarks>
internal readonly record struct TransitionCondition(int Node);

/// <summary>Opaque reference to one Boolean condition in a compiled Transition condition model.</summary>
/// <remarks>
/// A reference is meaningful only with the <see cref="TransitionConditionModel"/> that produced it.
/// The default value is invalid because it is not owned by any model.
/// </remarks>
public readonly record struct TransitionConditionRef
{
    internal TransitionConditionRef(TransitionConditionSolver owner, int node)
    {
        Owner = owner;
        Node = node;
    }

    internal TransitionConditionSolver? Owner { get; }

    internal int Node { get; }
}

/// <summary>
/// Immutable public proof surface for the canonical conditional access and effect model of one Transition.
/// </summary>
/// <remarks>
/// The compiler retains a reduced ordered binary decision diagram privately. Query operations are synchronized,
/// deterministic, and may populate private memoization caches without changing observable model semantics.
/// </remarks>
public sealed class TransitionConditionModel
{
    readonly object sync = new();
    readonly TransitionConditionSolver solver;

    internal TransitionConditionModel(TransitionConditionSolver solver)
    {
        this.solver = Guard.RequireNotNull(solver);
        Atoms = solver.GetAtomIds();
    }

    /// <summary>Stable atom identities used by this definition-owned condition model.</summary>
    public ImmutableArray<string> Atoms { get; }

    /// <summary>The condition that is never satisfiable.</summary>
    public TransitionConditionRef False => new(solver, 0);

    /// <summary>The condition satisfied by every activation in the model.</summary>
    public TransitionConditionRef True => new(solver, 1);

    /// <summary>Tests whether at least one assignment satisfies a condition.</summary>
    /// <param name="condition">Model-owned condition to inspect.</param>
    /// <returns><see langword="true"/> when the condition has a satisfying assignment.</returns>
    /// <exception cref="ArgumentException"><paramref name="condition"/> is not owned by this model.</exception>
    public bool IsSatisfiable(TransitionConditionRef condition)
    {
        lock (sync)
        {
            return solver.IsSatisfiable(ToInternal(condition));
        }
    }

    /// <summary>Tests whether a requirement is satisfiable within a supplied semantic domain.</summary>
    /// <param name="condition">Requirement or occurrence condition.</param>
    /// <param name="domain">Domain within which satisfiability is queried.</param>
    /// <returns><see langword="true"/> when both conditions can hold.</returns>
    /// <exception cref="ArgumentException">A condition is not owned by this model.</exception>
    public bool IsSatisfiableWithin(
        TransitionConditionRef condition,
        TransitionConditionRef domain)
    {
        lock (sync)
        {
            return !solver.AreMutuallyExclusive(ToInternal(condition), ToInternal(domain));
        }
    }

    /// <summary>Tests whether every assignment in one condition also satisfies another.</summary>
    /// <param name="premise">Condition establishing the query domain.</param>
    /// <param name="consequence">Condition required throughout <paramref name="premise"/>.</param>
    /// <returns><see langword="true"/> when the implication is proven.</returns>
    /// <exception cref="ArgumentException">A condition is not owned by this model.</exception>
    public bool Implies(TransitionConditionRef premise, TransitionConditionRef consequence)
    {
        lock (sync)
        {
            return solver.Implies(ToInternal(premise), ToInternal(consequence));
        }
    }

    /// <summary>Tests whether two conditions cannot occur on the same realized path.</summary>
    /// <param name="left">First model-owned condition.</param>
    /// <param name="right">Second model-owned condition.</param>
    /// <returns><see langword="true"/> when the conditions are mutually exclusive.</returns>
    /// <exception cref="ArgumentException">A condition is not owned by this model.</exception>
    public bool AreMutuallyExclusive(TransitionConditionRef left, TransitionConditionRef right)
    {
        lock (sync)
        {
            return solver.AreMutuallyExclusive(ToInternal(left), ToInternal(right));
        }
    }

    /// <summary>Classifies a condition as Must or May within an explicit semantic domain.</summary>
    /// <param name="condition">Requirement or occurrence condition.</param>
    /// <param name="domain">Semantic query domain.</param>
    /// <param name="strength">Receives Must when the domain implies the condition; otherwise May.</param>
    /// <returns>
    /// <see langword="false"/> when the domain is unsatisfiable or the condition is impossible within it;
    /// otherwise <see langword="true"/>.
    /// </returns>
    /// <exception cref="ArgumentException">A condition is not owned by this model.</exception>
    public bool TryGetStrength(
        TransitionConditionRef condition,
        TransitionConditionRef domain,
        out TransitionRequirementStrength strength)
    {
        lock (sync)
        {
            var internalCondition = ToInternal(condition);
            var internalDomain = ToInternal(domain);
            if (!solver.IsSatisfiable(internalDomain)
                || solver.AreMutuallyExclusive(internalCondition, internalDomain))
            {
                strength = default;
                return false;
            }

            strength = solver.Implies(internalDomain, internalCondition)
                ? TransitionRequirementStrength.Must
                : TransitionRequirementStrength.May;
            return true;
        }
    }

    /// <summary>Formats a condition as a deterministic canonical if-then-else expression.</summary>
    /// <param name="condition">Model-owned condition to format.</param>
    /// <returns>A derived diagnostic view of the condition.</returns>
    /// <exception cref="ArgumentException"><paramref name="condition"/> is not owned by this model.</exception>
    public string Format(TransitionConditionRef condition)
    {
        lock (sync)
        {
            return solver.Format(ToInternal(condition));
        }
    }

    TransitionCondition ToInternal(TransitionConditionRef condition)
    {
        if (!ReferenceEquals(condition.Owner, solver))
        {
            throw new ArgumentException("The condition is not owned by this Transition condition model.", nameof(condition));
        }

        var internalCondition = new TransitionCondition(condition.Node);
        solver.EnsureOwned(internalCondition, nameof(condition));
        return internalCondition;
    }
}

/// <summary>
/// Canonical Boolean condition algebra backed by a reduced ordered binary decision diagram.
/// </summary>
/// <remarks>
/// Atom order is derived deterministically from the initial catalog and subsequent semantic discovery order.
/// Instances retain memoized nodes and are intended for single-threaded compiler use.
/// </remarks>
internal sealed class TransitionConditionSolver
{
    const int FalseNode = 0;
    const int TrueNode = 1;
    const int TerminalVariable = int.MaxValue;

    readonly List<string> atomIds;
    readonly Dictionary<string, int> atomNodes;
    readonly List<DecisionNode> nodes;
    readonly Dictionary<DecisionNode, int> uniqueNodes;
    readonly Dictionary<int, int> negations;
    readonly Dictionary<ApplyKey, int> applications = [];

    /// <summary>Creates a solver over an initial deterministically ordered atom catalog.</summary>
    /// <param name="atomIds">Unique, non-empty stable atom identifiers known before semantic discovery.</param>
    /// <exception cref="ArgumentNullException"><paramref name="atomIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="atomIds"/> contains an empty, white-space, or duplicate identifier.
    /// </exception>
    internal TransitionConditionSolver(IEnumerable<string> atomIds)
    {
        ArgumentNullException.ThrowIfNull(atomIds);

        var normalizedAtomIds = atomIds.ToArray();
        if (normalizedAtomIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Transition condition atom identifiers cannot be empty or white space.",
                nameof(atomIds));
        }

        Array.Sort(normalizedAtomIds, StringComparer.Ordinal);
        for (var index = 1; index < normalizedAtomIds.Length; index++)
        {
            if (StringComparer.Ordinal.Equals(normalizedAtomIds[index - 1], normalizedAtomIds[index]))
            {
                throw new ArgumentException(
                    $"Transition condition atom '{normalizedAtomIds[index]}' is declared more than once.",
                    nameof(atomIds));
            }
        }

        this.atomIds = new List<string>(normalizedAtomIds);
        atomNodes = new Dictionary<string, int>(normalizedAtomIds.Length, StringComparer.Ordinal);
        nodes = new List<DecisionNode>(normalizedAtomIds.Length + 2)
        {
            default,
            default
        };
        uniqueNodes = new Dictionary<DecisionNode, int>(normalizedAtomIds.Length);
        negations = new Dictionary<int, int>(normalizedAtomIds.Length + 2)
        {
            [FalseNode] = TrueNode,
            [TrueNode] = FalseNode
        };

        for (var variable = 0; variable < normalizedAtomIds.Length; variable++)
        {
            atomNodes.Add(normalizedAtomIds[variable], Make(variable, FalseNode, TrueNode));
        }
    }

    /// <summary>The canonical condition that is never satisfiable.</summary>
    internal TransitionCondition False => new(FalseNode);

    /// <summary>The canonical condition that is always satisfied.</summary>
    internal TransitionCondition True => new(TrueNode);

    /// <summary>
    /// Returns a discovered atom, or appends a new atom after every previously discovered atom.
    /// </summary>
    /// <remarks>
    /// Dynamically appended atoms retain ordered-BDD validity because their variables follow every
    /// previously declared variable. Callers must discover them in deterministic semantic order.
    /// </remarks>
    /// <param name="atomId">Stable non-empty atom identity.</param>
    /// <returns>The positive Boolean condition represented by <paramref name="atomId"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="atomId"/> is empty or white space.</exception>
    internal TransitionCondition GetOrAddAtom(string atomId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(atomId);
        if (atomNodes.TryGetValue(atomId, out var existing))
        {
            return new(existing);
        }

        var variable = atomIds.Count;
        atomIds.Add(atomId);
        var node = Make(variable, FalseNode, TrueNode);
        atomNodes.Add(atomId, node);
        return new(node);
    }

    /// <summary>Snapshots stable atom identities in solver variable order.</summary>
    /// <returns>Immutable atom identities used to format and explain model conditions.</returns>
    internal ImmutableArray<string> GetAtomIds() => [.. atomIds];

    internal void EnsureOwned(TransitionCondition condition, string parameterName) =>
        EnsureKnown(condition, parameterName);

    /// <summary>Returns the logical negation of a condition.</summary>
    /// <param name="condition">Solver-owned condition to negate.</param>
    /// <returns>The canonical negated condition.</returns>
    /// <exception cref="ArgumentException"><paramref name="condition"/> does not identify a node in this solver.</exception>
    internal TransitionCondition Not(TransitionCondition condition)
    {
        EnsureKnown(condition, nameof(condition));
        return new(Negate(condition.Node));
    }

    /// <summary>Returns the logical conjunction of two conditions.</summary>
    /// <param name="left">Solver-owned left condition.</param>
    /// <param name="right">Solver-owned right condition.</param>
    /// <returns>The canonical condition that holds when both operands hold.</returns>
    /// <exception cref="ArgumentException"><paramref name="left"/> or <paramref name="right"/> does not identify a node in this solver.</exception>
    internal TransitionCondition And(TransitionCondition left, TransitionCondition right)
    {
        EnsureKnown(left, nameof(left));
        EnsureKnown(right, nameof(right));
        return new(Apply(BinaryOperation.And, left.Node, right.Node));
    }

    /// <summary>Returns the logical disjunction of two conditions.</summary>
    /// <param name="left">Solver-owned left condition.</param>
    /// <param name="right">Solver-owned right condition.</param>
    /// <returns>The canonical condition that holds when either operand holds.</returns>
    /// <exception cref="ArgumentException"><paramref name="left"/> or <paramref name="right"/> does not identify a node in this solver.</exception>
    internal TransitionCondition Or(TransitionCondition left, TransitionCondition right)
    {
        EnsureKnown(left, nameof(left));
        EnsureKnown(right, nameof(right));
        return new(Apply(BinaryOperation.Or, left.Node, right.Node));
    }

    /// <summary>Tests whether at least one atom assignment satisfies a condition.</summary>
    /// <param name="condition">Solver-owned condition to inspect.</param>
    /// <returns><see langword="true"/> unless the condition is canonically false.</returns>
    /// <exception cref="ArgumentException"><paramref name="condition"/> does not identify a node in this solver.</exception>
    internal bool IsSatisfiable(TransitionCondition condition)
    {
        EnsureKnown(condition, nameof(condition));
        return condition.Node != FalseNode;
    }

    /// <summary>Tests whether every assignment satisfying one condition also satisfies another.</summary>
    /// <param name="premise">Solver-owned implication premise.</param>
    /// <param name="consequence">Solver-owned implication consequence.</param>
    /// <returns><see langword="true"/> when <paramref name="premise"/> implies <paramref name="consequence"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="premise"/> or <paramref name="consequence"/> does not identify a node in this solver.</exception>
    internal bool Implies(TransitionCondition premise, TransitionCondition consequence)
    {
        EnsureKnown(premise, nameof(premise));
        EnsureKnown(consequence, nameof(consequence));
        return Apply(BinaryOperation.And, premise.Node, Negate(consequence.Node)) == FalseNode;
    }

    /// <summary>Tests whether two conditions cannot be satisfied by the same assignment.</summary>
    /// <param name="left">Solver-owned left condition.</param>
    /// <param name="right">Solver-owned right condition.</param>
    /// <returns><see langword="true"/> when the operands are mutually exclusive.</returns>
    /// <exception cref="ArgumentException"><paramref name="left"/> or <paramref name="right"/> does not identify a node in this solver.</exception>
    internal bool AreMutuallyExclusive(TransitionCondition left, TransitionCondition right)
    {
        EnsureKnown(left, nameof(left));
        EnsureKnown(right, nameof(right));
        return Apply(BinaryOperation.And, left.Node, right.Node) == FalseNode;
    }

    /// <summary>Tests whether two conditions have the same truth value for every assignment.</summary>
    /// <param name="left">Solver-owned left condition.</param>
    /// <param name="right">Solver-owned right condition.</param>
    /// <returns><see langword="true"/> when the operands are logically equivalent.</returns>
    /// <exception cref="ArgumentException"><paramref name="left"/> or <paramref name="right"/> does not identify a node in this solver.</exception>
    internal bool Equivalent(TransitionCondition left, TransitionCondition right)
    {
        EnsureKnown(left, nameof(left));
        EnsureKnown(right, nameof(right));
        return left.Node == right.Node;
    }

    /// <summary>Formats a condition as a deterministic canonical if-then-else expression.</summary>
    /// <param name="condition">Solver-owned condition to format.</param>
    /// <returns>
    /// <c>false</c>, <c>true</c>, or a canonical <c>ite("atom", whenTrue, whenFalse)</c> expression.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="condition"/> does not identify a node in this solver.</exception>
    internal string Format(TransitionCondition condition)
    {
        EnsureKnown(condition, nameof(condition));
        var cache = new Dictionary<int, string>
        {
            [FalseNode] = "false",
            [TrueNode] = "true"
        };
        return Format(condition.Node, cache);
    }

    int Make(int variable, int low, int high)
    {
        if (low == high)
        {
            return low;
        }

        var candidate = new DecisionNode(variable, low, high);
        if (uniqueNodes.TryGetValue(candidate, out var existing))
        {
            return existing;
        }

        var node = nodes.Count;
        nodes.Add(candidate);
        uniqueNodes.Add(candidate, node);
        return node;
    }

    int Negate(int node)
    {
        if (negations.TryGetValue(node, out var known))
        {
            return known;
        }

        var decision = nodes[node];
        var result = Make(decision.Variable, Negate(decision.Low), Negate(decision.High));
        negations.Add(node, result);
        negations.TryAdd(result, node);
        return result;
    }

    int Apply(BinaryOperation operation, int left, int right)
    {
        if (left > right)
        {
            (left, right) = (right, left);
        }

        if (left == right)
        {
            return left;
        }

        switch (operation)
        {
            case BinaryOperation.And when left == FalseNode:
                return FalseNode;
            case BinaryOperation.And when left == TrueNode:
                return right;
            case BinaryOperation.Or when left == FalseNode:
                return right;
            case BinaryOperation.Or when left == TrueNode:
                return TrueNode;
        }

        var key = new ApplyKey(operation, left, right);
        if (applications.TryGetValue(key, out var known))
        {
            return known;
        }

        var leftVariable = Variable(left);
        var rightVariable = Variable(right);
        var variable = Math.Min(leftVariable, rightVariable);
        var leftLow = leftVariable == variable ? nodes[left].Low : left;
        var leftHigh = leftVariable == variable ? nodes[left].High : left;
        var rightLow = rightVariable == variable ? nodes[right].Low : right;
        var rightHigh = rightVariable == variable ? nodes[right].High : right;

        var low = Apply(operation, leftLow, rightLow);
        var high = Apply(operation, leftHigh, rightHigh);
        var result = Make(variable, low, high);
        applications.Add(key, result);
        return result;
    }

    int Variable(int node) => node <= TrueNode ? TerminalVariable : nodes[node].Variable;

    string Format(int node, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(node, out var known))
        {
            return known;
        }

        var decision = nodes[node];
        var builder = new StringBuilder();
        builder.Append("ite(");
        AppendQuoted(builder, atomIds[decision.Variable]);
        builder.Append(',');
        builder.Append(Format(decision.High, cache));
        builder.Append(',');
        builder.Append(Format(decision.Low, cache));
        builder.Append(')');
        var result = builder.ToString();
        cache.Add(node, result);
        return result;
    }

    static void AppendQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case < ' ':
                    builder.Append("\\u");
                    builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
    }

    void EnsureKnown(TransitionCondition condition, string parameterName)
    {
        if ((uint)condition.Node >= (uint)nodes.Count)
        {
            throw new ArgumentException(
                "The transition condition does not identify a node owned by this solver.",
                parameterName);
        }
    }

    readonly record struct DecisionNode(int Variable, int Low, int High);

    readonly record struct ApplyKey(BinaryOperation Operation, int Left, int Right);

    enum BinaryOperation : byte
    {
        And,
        Or
    }
}
