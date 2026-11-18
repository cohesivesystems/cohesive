using System.Text.Json;
using Cohesive.Examples.TenderAcceptance;
using Cohesive.Machines;
using Cohesive.Model;

var tender = TenderAcceptanceMachines.Tender();
var overlap = Machine.Define("tender.overlap-probe", "1");
var offered = overlap.State("offered").Initial();
var accepted = overlap.State("accepted").Final();
foreach (var movement in new[] { "first", "second" })
    overlap.Edge(movement).On("send-result").From(offered).To(accepted)
        .When((TenderState facts, SendResult input) => input.LinkedLoadExists);

var models = new[] { tender.Definition, overlap.Build() };
var fixtures = models.Select(model =>
{
    var cases = from state in model.States
                from action in model.Edges.Select(edge => edge.Action).Distinct().Append("unknown")
                from cancelled in new[] { false, true }
                from input in new object[] { new SendResult(false), new SendResult(true), new SendResult(false, false),
                    new Receipt(true, true), new Receipt(false, true), new { } }
                let facts = new TenderState("load-1", false, false, cancelled, "")
                let decision = model.Evaluate(state, action, ObservationValue.FromObject(facts), ObservationValue.FromObject(input))
                select new { state, action, facts, input, kind = decision.Kind.ToString(), movement = decision.Edge?.Id,
                    next = decision.Edge?.To ?? state };
    return new { document = JsonSerializer.Deserialize<JsonElement>(model.CanonicalJson), cases };
});
File.WriteAllText(args.Single(), JsonSerializer.Serialize(fixtures));
