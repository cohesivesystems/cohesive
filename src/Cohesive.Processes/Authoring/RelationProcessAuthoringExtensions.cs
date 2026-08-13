using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;

namespace Cohesive.Processes.Authoring;

/// <summary>Projects typed canonical Relations into exact Process linking evidence.</summary>
public static class RelationProcessAuthoringExtensions
{
    /// <summary>Derives Process linking evidence from a typed canonical Relation.</summary>
    /// <typeparam name="TInput">CLR type of the rooted Relation input.</typeparam>
    /// <typeparam name="TResult">CLR type of the singular Relation result.</typeparam>
    /// <param name="relation">Typed canonical Relation that remains authoritative for the derived link.</param>
    /// <returns>Exact Relation identity and portable input/result contracts for Process validation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relation"/> is <see langword="null"/>.</exception>
    public static ProcessDefinitionLink CreateProcessDefinitionLink<TInput, TResult>(
        this Relation<TInput, TResult> relation)
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(relation);
        return new(
            relation.Reference,
            ProcessDefinitionLinkKind.RelationQuery,
            relation.InputContract,
            relation.ResultContract);
    }

    /// <summary>Derives Process linking evidence from a typed canonical hosted Query.</summary>
    /// <typeparam name="TInput">CLR type of the hosted Query invocation input.</typeparam>
    /// <typeparam name="TResult">CLR type of the hosted Query singular result.</typeparam>
    /// <param name="query">Typed canonical hosted Query that remains authoritative for the derived link.</param>
    /// <returns>Exact hosted-Query identity and portable input/result contracts for Process validation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The hosted Query has validation errors.</exception>
    public static ProcessDefinitionLink CreateProcessDefinitionLink<TInput, TResult>(
        this HostedQuery<TInput, TResult> query)
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsValid)
        {
            throw new InvalidOperationException(
                "Process linking evidence cannot be derived from an invalid hosted Query.");
        }

        return new(
            query.Reference,
            ProcessDefinitionLinkKind.RelationQuery,
            query.InputContract,
            query.ResultContract);
    }
}
