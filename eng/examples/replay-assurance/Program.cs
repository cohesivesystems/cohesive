using System.Text.Json;
using Cohesive.Assurance;
using Cohesive.Examples.ReplayAssurance;

if (args is ["--export-tlc", var directory])
{
    ReservationFormalExport.Write(directory);
    return 0;
}

var connection = Environment.GetEnvironmentVariable(ReservationReplayDemo.ConnectionVariable);
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine($"Set {ReservationReplayDemo.ConnectionVariable} to a disposable PostgreSQL database, or run ./eng/examples/replay-assurance/run-local.sh.");
    return 2;
}
if (args.Length > 1)
{
    Console.Error.WriteLine("Usage: ReplayAssuranceDemo [output-directory]");
    return 2;
}
var output = Path.GetFullPath(args.Length == 1 ? args[0] : Path.Combine("artifacts", "replay-assurance", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(output);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
foreach (var realization in Enum.GetValues<ReservationRealization>())
{
    var result = await ReservationReplayDemo.RunAsync(connection, realization, timeout.Token);
    var evidence = EffectUniquenessEvaluator.Evaluate(result.Check);
    Console.WriteLine($"\n{realization}: reserve {ReservationReplayDemo.Quantity} from {ReservationReplayDemo.InitialAvailable}, lose acknowledgement, recreate client, retry");
    Console.WriteLine($"Available: {result.Available}; committed updates: {result.Effects.Length}; receipts: {result.ReceiptCount}; retry: {result.ReplayDisposition?.ToString() ?? "unprotected write"}");
    Console.WriteLine(evidence.Explain());
    var name = realization.ToString();
    await WriteArtifact(name + ".evidence.json", EffectUniquenessDocument.Serialize(result.Check));
    await WriteArtifact(name + ".audit.json", JsonSerializer.Serialize(result.Effects, new JsonSerializerOptions { WriteIndented = true }));
    await WriteArtifact(name + ".txt", evidence.Explain());
    var expected = realization == ReservationRealization.AtomicReceipt ? AssuranceVerdict.Satisfied : AssuranceVerdict.Violated;
    var expectedStock = ReservationReplayDemo.InitialAvailable - ReservationReplayDemo.Quantity * (realization == ReservationRealization.AtomicReceipt ? 1 : 2);
    if (evidence.Verdict != expected || result.Available != expectedStock)
        throw new InvalidOperationException("Demo failed its expected positive or negative control.");
}
Console.WriteLine($"\nRetained artifacts: {output}\nBoth owned schemas removed. Results apply to these closed fixture scopes, not all executions.");
return 0;

async Task WriteArtifact(string name, string content)
{
    // Existing evidence is immutable; an explicit reused output directory must not overwrite it.
    await using var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write, FileShare.None,
        bufferSize: 4096, useAsync: true);
    await using var writer = new StreamWriter(stream);
    await writer.WriteAsync(content.AsMemory(), timeout.Token);
}
