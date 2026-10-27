using System.Text.Json;
using AspireFirst.Orders;
using Cohesive.Examples.ReplayAssurance;

namespace Cohesive.Assurance.Tests;

public sealed class ReservationFormalExportTests
{
    [Fact]
    public void Export_is_deterministic_and_pins_the_native_claim_transition()
    {
        var root = Path.Combine(Path.GetTempPath(), "cohesive-formal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            ReservationFormalExport.Write(first);
            ReservationFormalExport.Write(second);
            foreach (var file in Directory.GetFiles(first))
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(second, Path.GetFileName(file))));
            var claim = JsonSerializer.Deserialize<EffectUniquenessClaim>(File.ReadAllText(Path.Combine(first, "claim.json")))!;
            var transition = new FulfillmentDomain().InventoryTransitions.Reserve.Compile().Plan!.DefinitionReference;
            Assert.Equal(ReservationClaim.Create(transition), claim);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "manifest.json")));
            Assert.Equal("ReferenceModel", manifest.RootElement.GetProperty("TargetKind").GetString());
            // Retained evidence cannot silently be replaced by another export.
            Assert.Throws<IOException>(() => ReservationFormalExport.Write(first));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
