using System.Collections.Immutable;
using Cohesive.Model;

namespace Cohesive.Relations.Queries;

static class EntityPredicatePlanner
{
    public static EntityPredicate And(EntityPredicate left, EntityPredicate right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var normalizedLeft = LiftScope(left);
        var normalizedRight = LiftScope(right);
        return new(new And<FieldPredicate>([normalizedLeft.Predicate.Normalize(), normalizedRight.Predicate.Normalize()]));
    }

    public static EntityPredicate LiftScope(EntityPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (predicate.Scope is null)
            return predicate with { Predicate = predicate.Predicate.Normalize() };

        var scope = predicate.Scope.Value;
        return new(
            Predicate: new FieldPredicate(ToCollectionField(scope), new AnyFieldPredicate(predicate.Predicate.Normalize())));
    }

    public static IReadOnlySet<string> GetRequiredTopLevelFields(EntityPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        HashSet<string> fields = new(StringComparer.Ordinal);
        if (predicate.Scope is { } scope && TryGetLeadingFieldIdentity(scope, out var scopeField))
            fields.Add(scopeField);

        VisitFieldPredicate(predicate.Predicate);
        return fields;

        void VisitFieldPredicate(BoolExpr<FieldPredicate> expr)
        {
            switch (expr)
            {
                case Atom<FieldPredicate> atom:
                    if (TryGetLeadingFieldIdentity(atom.Term.Field, out var field))
                        fields.Add(field);
                    break;
                case And<FieldPredicate> conjunction:
                    foreach (var term in conjunction.Terms)
                        VisitFieldPredicate(term);
                    break;
                case Or<FieldPredicate> disjunction:
                    foreach (var term in disjunction.Terms)
                        VisitFieldPredicate(term);
                    break;
                case Not<FieldPredicate> negation:
                    VisitFieldPredicate(negation.Term);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.");
            }
        }
    }

    static FieldPath ToCollectionField(FieldPath scope)
    {
        var segments = ImmutableArray.CreateBuilder<FieldPathSegment>();
        var sawElement = false;
        foreach (var segment in scope.Segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Field:
                    if (sawElement)
                    {
                        throw new NotSupportedException(
                            $"Scoped predicate '{scope}' cannot navigate beyond an element segment without another explicit '{nameof(AnyFieldPredicate)}'.");
                    }

                    segments.Add(segment);
                    break;
                case SegmentKind.Element:
                    if (sawElement)
                        throw new NotSupportedException($"Scoped predicate '{scope}' contains multiple element segments.");

                    sawElement = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported field-path segment kind '{segment.Kind}'.");
            }
        }

        return new([.. segments]);
    }

    static bool TryGetLeadingFieldIdentity(FieldPath path, out string fieldIdentity)
    {
        foreach (var segment in path.Segments)
        {
            if (!segment.TryGetFieldIdentity(out fieldIdentity))
                continue;

            return true;
        }

        fieldIdentity = string.Empty;
        return false;
    }
}
