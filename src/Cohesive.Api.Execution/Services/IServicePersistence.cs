using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage;

namespace Cohesive.Api.Execution.Services;

/// <summary>Host-owned resolution of repositories and prepared queries from registered storage mappings.</summary>
/// <remarks>Implementations preserve exact entity authority and explicit placement. Resolution happens at setup;
/// implementations must support concurrent resolution and must not own or dispose caller-supplied data sources.</remarks>
public interface IServicePersistence
{
    /// <summary>Resolves a repository for an exact canonical observation contract.</summary>
    /// <param name="observation">Required entity observation contract.</param>
    /// <returns>A shared repository using the registered entity authority.</returns>
    IEntityRepository Repository(ValueContract observation);

    /// <summary>Prepares a query using explicit native placement and complete-result bounds.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Query result.</typeparam>
    /// <param name="query">Canonical query and presentation projection.</param>
    /// <param name="maximumRows">Complete-result row bound.</param><param name="maximumBytes">Decoded-value bound.</param>
    /// <param name="consistency">Required consistency across every read contributing to the result.</param>
    /// <returns>A host-lifetime reader; invocation data is never cached.</returns>
    IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query, int maximumRows, long maximumBytes, RelationQueryConsistency consistency);
}
