using System.Text;
using Cohesive.Cli;
using Cohesive.Configuration;

EchoCommand? captured = null;
var streamsMatched = false;
await using MemoryStream standardInput = new(Encoding.UTF8.GetBytes("fixture"));
await using MemoryStream standardOutput = new();
using StringWriter standardError = new();
var io = CommandIo.Null(
    standardInput: standardInput,
    standardOutput: standardOutput,
    standardError: standardError);
var app = new CliApplication(description: "Package smoke", io);
app.Command<EchoCommand>("echo", "Echo one value")
    .RequireExactlyOne(command => command.Value, command => command.AlternateValue)
    .Validate(ValidateEcho)
    .OnExecute(async (CliCommandContext<EchoCommand> context) =>
    {
        captured = context.Configuration;
        streamsMatched = ReferenceEquals(io, context.Io)
            && ReferenceEquals(standardInput, context.Io.StandardInput)
            && ReferenceEquals(standardOutput, context.Io.StandardOutput);
        var input = await context.Io.ReadUtf8TextAsync(CommandIo.StandardStreamPath);
        context.Io.WriteLine($"{context.Configuration.Value}:{input}");
        return 0;
    });

var exitCode = await app.RunAsync(["echo", "--value", "ready"]);
return exitCode == 0
       && Encoding.UTF8.GetString(standardOutput.ToArray()).Trim() == "ready:fixture"
       && standardError.ToString().Length == 0
       && captured?.Value == "ready"
       && streamsMatched
    ? 0
    : 1;

static IReadOnlyList<string> ValidateEcho(EchoCommand command) =>
    command.Value.Length <= 20
        ? []
        : ["Echo value is too long."];

sealed class EchoCommand
{
    [ConfigurationParameter("value", CliKey = "value")]
    public string Value { get; init; } = string.Empty;

    [ConfigurationParameter("alternate-value", CliKey = "alternate-value")]
    public string AlternateValue { get; init; } = string.Empty;
}
