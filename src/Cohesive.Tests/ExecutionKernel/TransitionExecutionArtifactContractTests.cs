using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class TransitionExecutionArtifactContractTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void MachineEdgeLink_NormalizesAssignmentsByStructuralFieldPath()
    {
        FieldPath nested = new([FieldPathSegment.ForField("a"), FieldPathSegment.ForField("b")]);
        FieldPath dottedField = new([FieldPathSegment.ForField("a.b")]);
        Assert.Equal(nested.ToString(), dottedField.ToString());

        var link = Link(
            Reference("sha", "canonical", "01"),
            new("edge"),
            [Assignment(dottedField), Assignment(nested)]);

        Assert.Equal([nested, dottedField], link.Assignments.Select(static value => value.Path));
    }

    [Fact]
    public void MachineLinkCatalog_NormalizesExactFingerprintsByCompleteStructuralTuple()
    {
        ExecutionDefinitionReference[] references =
        [
            Reference("z-algorithm", "a-canonicalization", "00"),
            Reference("a-algorithm", "z-canonicalization", "00"),
            Reference("a-algorithm", "a-canonicalization", "ff"),
            Reference("a-algorithm", "a-canonicalization", "00")
        ];
        var links = references
            .Select((reference, index) => Link(
                reference,
                new($"edge/{index}"),
                [Assignment(FieldPath.FromField($"state{index}"))]))
            .ToArray();

        var catalog = new TransitionMachineLinkCatalog([.. links.Reverse()]);

        Assert.Equal(
            [
                ("a-algorithm", "a-canonicalization", "00"),
                ("a-algorithm", "a-canonicalization", "ff"),
                ("a-algorithm", "z-canonicalization", "00"),
                ("z-algorithm", "a-canonicalization", "00")
            ],
            catalog.Edges.Select(static value => (
                value.Machine.Fingerprint.Algorithm,
                value.Machine.Fingerprint.Canonicalization,
                value.Machine.Fingerprint.Value)));
    }

    [Fact]
    public void Compile_NormalizesUsedMachineLinksWithCatalogOrdering()
    {
        var later = Reference("z-algorithm", "canonical", "00");
        var earlier = Reference("a-algorithm", "canonical", "ff");
        var laterLink = Link(later, new("edge/later"), [Assignment(FieldPath.FromField("later"))]);
        var earlierLink = Link(earlier, new("edge/earlier"), [Assignment(FieldPath.FromField("earlier"))]);
        var definition = new CanonicalTransitionDefinition(
            new(new ObjectTypeRef([])),
            new(new ObjectTypeRef(
            [
                new(new("earlier"), StringContract.Type!),
                new(new("later"), StringContract.Type!)
            ])),
            StringContract,
            [],
            new(
                new("root"),
                [
                    new MoveMachineTransitionNode(
                        new("move/later"),
                        later,
                        laterLink.Edge,
                        Expr.Const("later-rejected")),
                    new MoveMachineTransitionNode(
                        new("move/earlier"),
                        earlier,
                        earlierLink.Edge,
                        Expr.Const("earlier-rejected")),
                    new OutcomeTransitionNode(
                        new("outcome"),
                        TransitionOutcomeDisposition.Applied,
                        Expr.Const("applied"))
                ]));
        var document = TransitionDefinitionDocuments.Create(
            new("transition/machine-order"),
            new("revision/1"),
            definition,
            new(
                new("transition-execution-artifact-contract-tests", "1"),
                new("tests/execution-kernel/transition-execution-artifact-contract"),
                DocumentOrigin.Generated));

        var compilation = TransitionStaticCompiler.Compile(
            document,
            machineLinks: new([laterLink, earlierLink]));

        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        Assert.Equal([earlier, later], compilation.Plan!.MachineEdges.Select(static value => value.Machine));
    }

    [Fact]
    public void MachineMovement_RejectsInvalidArtifactIdentityAndAssignments()
    {
        var machine = Reference("sha", "canonical", "00");
        ExecutionNodeId node = new("move");
        ExecutionNodeId edge = new("edge");

        Assert.Equal(
            "node",
            Assert.Throws<ArgumentException>(() => new TransitionMachineMovement(default, machine, edge, [])).ParamName);
        Assert.Equal(
            "edge",
            Assert.Throws<ArgumentException>(() => new TransitionMachineMovement(node, machine, default, [])).ParamName);
        Assert.Equal(
            "machine",
            Assert.Throws<ArgumentNullException>(() => new TransitionMachineMovement(node, null!, edge, [])).ParamName);
        Assert.Equal(
            "assignments",
            Assert.Throws<ArgumentException>(() => new TransitionMachineMovement(node, machine, edge, default)).ParamName);
        Assert.Equal(
            "assignments",
            Assert.Throws<ArgumentException>(() => new TransitionMachineMovement(
                node,
                machine,
                edge,
                ImmutableArray.Create<TransitionExecutedPatch>((TransitionExecutedPatch)null!))).ParamName);
    }

    [Fact]
    public void ExecutionEvidence_RejectsInvalidProvenanceAndTraceEntries()
    {
        var definition = Reference("sha", "canonical", "00");
        ActivationId activation = new("activation/1");

        Assert.Equal(
            "definition",
            Assert.Throws<ArgumentNullException>(() => new TransitionExecutionEvidence(null!, activation, [])).ParamName);
        Assert.Equal(
            "activation",
            Assert.Throws<ArgumentException>(() => new TransitionExecutionEvidence(definition, default, [])).ParamName);
        Assert.Equal(
            "trace",
            Assert.Throws<ArgumentException>(() => new TransitionExecutionEvidence(definition, activation, default)).ParamName);
        Assert.Equal(
            "trace",
            Assert.Throws<ArgumentException>(() => new TransitionExecutionEvidence(
                definition,
                activation,
                ImmutableArray.Create<TransitionTraceEvent>((TransitionTraceEvent)null!))).ParamName);
    }

    [Fact]
    public void ObservationConflict_RejectsEveryNullEvidenceComponent()
    {
        var access = TransitionObservationAccess.At(FieldPath.FromField("status"));
        var value = PortableValue.Concrete(StringContract, ObservationValue.FromString("pending"));

        Assert.Equal(
            "access",
            Assert.Throws<ArgumentNullException>(() => new TransitionObservationConflict(null!, value, value)).ParamName);
        Assert.Equal(
            "expected",
            Assert.Throws<ArgumentNullException>(() => new TransitionObservationConflict(access, null!, value)).ParamName);
        Assert.Equal(
            "observed",
            Assert.Throws<ArgumentNullException>(() => new TransitionObservationConflict(access, value, null!)).ParamName);
    }

    static TransitionMachineEdgeLink Link(
        ExecutionDefinitionReference machine,
        ExecutionNodeId edge,
        ImmutableArray<TransitionMachineConfigurationAssignment> assignments) => new(
        machine,
        edge,
        Expr.Const(true),
        Expr.Const(true),
        assignments);

    static TransitionMachineConfigurationAssignment Assignment(FieldPath path) => new(
        path,
        PortableValue.Concrete(StringContract, ObservationValue.FromString("ready")));

    static ExecutionDefinitionReference Reference(
        string algorithm,
        string canonicalization,
        string value) => new(
        new("machine/reference"),
        new("revision/1"),
        new(algorithm, canonicalization, value));

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
