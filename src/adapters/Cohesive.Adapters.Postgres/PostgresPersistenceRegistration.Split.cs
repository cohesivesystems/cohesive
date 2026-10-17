using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Adapters.Postgres;

public sealed partial class PostgresPersistenceRegistration
{
    // Preparation only: retain the validated cut in the reader, never rediscover it per invocation.
    RelationQuerySubplan? InferComposedPrefix(RelationQueryCompilationResult original, Func<QualifiedShapeId, bool> isRemote)
    {
        if (!original.IsSuccessful) throw new RelationQueryPreparationException("semantic", original);
        var request = original.Request;
        if (request.DefinitionDocument.Definition is not QueryDefinition definition) return null;
        if (definition.Results.Length != 1 || definition.Results[0] is not RowsQueryResultDefinition
            || request.Demand.Kind != RelationQueryCompilationDemandKind.AllDeclaredOutputs)
            return null;

        List<RelationQuerySubplan> candidates = [];
        foreach (var projection in definition.Body.Nodes.OfType<ProjectQueryNode>()
            .Where(node => node.Id != definition.Results[0].Input))
        {
            RelationQuerySubplan cut;
            try { cut = RelationQuerySubplan.Compile(original, projection.Id); }
            catch (RelationQueryPreparationException) { continue; } // An invalid cut is not an invalid original query.
            var native = Inputs(cut.Prefix);
            if (!native.Any() || native.Any(shape => !bindings.Tables.ContainsKey(shape) || isRemote(shape))) continue;
            var remaining = cut.Remainder.Plan!.InputContract;
            var remoteShapes = remaining.Sources.Where(input => input.Node != cut.Cut.Id).Select(input => input.Shape)
                .Concat(remaining.Traversals.Select(input => input.ResultShape)).ToArray();
            if (remoteShapes.Length == 0 || remoteShapes.Any(shape => !isRemote(shape))) continue;
            candidates.Add(cut);
        }
        // Prefer the largest closed native prefix; never choose between incomparable regions by node order.
        var maximal = candidates.Where(candidate => !candidates.Any(other => other != candidate
            && candidate.CoveredNodes.All(other.CoveredNodes.Contains))).ToArray();
        if (maximal.Length > 1)
            throw new RelationQueryPreparationException("composed split", original,
                code: "postgres.composed.splitAmbiguous", detail: "Several closed native prefixes match placement. Select an explicit projection boundary.");
        return maximal.SingleOrDefault();

        static IEnumerable<QualifiedShapeId> Inputs(RelationQueryCompilationResult compilation) =>
            compilation.Plan!.InputContract.Sources.Select(input => input.Shape)
                .Concat(compilation.Plan.InputContract.Traversals.Select(input => input.ResultShape));
    }
}
