using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text.Json;
using AspireFirst.Orders;
using Cohesive.Assurance;
using Cohesive.Model.Serialization;

namespace Cohesive.Examples.ReplayAssurance;

/// <summary>Example-local interpretation of the effect-uniqueness profile into a finite TLC model.</summary>
/// <remarks>Protocol correspondence is documented, not mechanically proven. No generic temporal claim language is implied.</remarks>
public static class ReservationFormalExport
{
    /// <summary>Exports immutable model inputs bound to the same claim as the native demo.</summary>
    /// <param name="directory">New output directory; existing files are never overwritten.</param>
    /// <exception cref="InvalidOperationException">The reservation transition does not compile.</exception>
    /// <exception cref="IOException">An output file exists or cannot be written.</exception>
    public static void Write(string directory)
    {
        var compilation = new FulfillmentDomain().InventoryTransitions.Reserve.Compile();
        if (!compilation.Validation.IsValid) throw new InvalidOperationException("Reservation did not compile.");
        var plan = compilation.Plan!;
        var claim = ReservationClaim.Create(plan.DefinitionReference);
        Directory.CreateDirectory(directory);
        var claimBytes = StrictDocumentJson.GetCanonicalBytes(claim, new JsonSerializerOptions());
        WriteFile("claim.json", System.Text.Encoding.UTF8.GetString(claimBytes));
        WriteFile("AssuranceClaim.tla", """
            -------------------------- MODULE AssuranceClaim --------------------------
            EXTENDS Naturals, Sequences, FiniteSets
            \* Generated interpretation of EffectUniquenessClaim. Each sequence position
            \* is a distinct durable effect; its value is the exact operation identity.
            AtMostOneEffect(effects) ==
                \A op \in {effects[i] : i \in 1..Len(effects)} :
                    Cardinality({i \in 1..Len(effects) : effects[i] = op}) <= 1
            =============================================================================
            """);
        WriteFile("ReservationProtocol.tla", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "formal", "ReservationProtocol.tla")));
        // Differential fixtures: every sequence of up to four effects across two operations.
        // Closed, complete and consistent observations are the model's supported domain.
        var cases = new List<string>();
        for (var length = 0; length <= 4; length++)
        for (var bits = 0; bits < (1 << length); bits++)
        {
            var sequence = Enumerable.Range(0, length).Select(i => 1 + ((bits >> i) & 1)).ToArray();
            var observations = sequence.Select((op, i) => new CommittedEffectObservation(
                op.ToString(System.Globalization.CultureInfo.InvariantCulture),
                i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToImmutableArray();
            var check = new EffectUniquenessCheck(claim,
                new("tlc/conformance", "1", "sequence-position/v1", "fixture", "stock"),
                new(AssuranceTargetKind.ReferenceModel, "tlc/conformance", "1", "finite-sequences/v1"),
                claim.Transition, ObservationScopeCompletion.ClosedComplete, observations);
            var expected = EffectUniquenessEvaluator.Evaluate(check).Verdict == AssuranceVerdict.Satisfied;
            cases.Add($"[effects |-> <<{string.Join(",", sequence)}>>, expected |-> {(expected ? "TRUE" : "FALSE")}]");
        }
        WriteFile("ClaimConformance.tla", $$"""
            ------------------------- MODULE ClaimConformance -------------------------
            EXTENDS AssuranceClaim
            Cases == <<{{string.Join(",\n", cases)}}>>
            VARIABLE sample
            Init == sample \in 1..Len(Cases)
            Next == UNCHANGED sample
            Conformance == AtMostOneEffect(Cases[sample].effects) = Cases[sample].expected
            =============================================================================
            """);
        WriteFile("ClaimConformance.cfg", "INIT Init\nNEXT Next\nINVARIANT Conformance\nCHECK_DEADLOCK FALSE\n");
        const int clients = 2, operations = 2, attempts = 2;
        foreach (var mode in new[] { "AtomicReceipt", "UnprotectedRetry", "SplitReceipt", "LostAckReplayWitness" })
        {
            var witness = mode == "LostAckReplayWitness";
            WriteFile(mode + ".cfg", $$"""
                SPECIFICATION Spec
                CONSTANTS
                  ClientCount = {{clients}}
                  OperationCount = {{operations}}
                  MaxAttempts = {{attempts}}
                  InitialStock = {{ReservationReplayDemo.InitialAvailable}}
                  Quantity = {{ReservationReplayDemo.Quantity}}
                  Mode = "{{(witness ? "AtomicReceipt" : mode)}}"
                INVARIANTS TypeOK StockAccounting {{(witness ? "NoLostAckReplay" : "Claim")}}
                CHECK_DEADLOCK FALSE
                """);
        }
        WriteFile("manifest.json", JsonSerializer.Serialize(new
        {
            Profile = "cohesive-demo/tlc-effect-uniqueness/v1",
            ClaimProfile = EffectUniquenessEvaluator.Profile,
            Claim = claim,
            ClaimFingerprint = Convert.ToHexStringLower(SHA256.HashData(claimBytes)),
            TargetKind = AssuranceTargetKind.ReferenceModel.ToString(),
            Bounds = new { Clients = clients, Operations = operations, AttemptsPerClient = attempts,
                InitialStock = ReservationReplayDemo.InitialAvailable, Quantity = ReservationReplayDemo.Quantity },
            Correspondence = "Manual abstraction; no implementation refinement proof",
            Assumptions = new[] { "Stable exact operation identity and identical request on retry",
                "Single authority, subject and fixed reservation transition; no unrelated writers",
                "Atomic durable state-and-receipt commit with unique occurrence fence in AtomicReceipt",
                "Receipts retained for the whole run; no corruption or identity collisions",
                "All durable effects observed once; sequence index is physical effect identity",
                "No fairness or liveness claim; attempts bounded by construction" }
        }, new JsonSerializerOptions { WriteIndented = true }));

        void WriteFile(string name, string content)
        {
            using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew);
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }
    }
}
