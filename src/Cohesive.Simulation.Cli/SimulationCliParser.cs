using System.Globalization;
using Cohesive.Simulation.Provisioning;

namespace Cohesive.Simulation.Cli;

static class SimulationCliParser
{
    public static bool TryParse(
        string[] args,
        out SimulationCliOptions? options,
        out string? error,
        out bool showHelp)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        error = null;
        showHelp = false;
        if (args.Length == 0 || IsHelp(args[0]))
        {
            showHelp = true;
            return false;
        }

        if (!string.Equals(args[0], "provision", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unknown command '{args[0]}'.";
            return false;
        }

        string? worldPath = null;
        string outputPath = SimulationCliOptions.StandardStreamPath;
        string? targetId = null;
        long rootSeed = default;
        var rootSeedSpecified = false;
        var batchSize = WorldProvisioningOptions.DefaultBatchSize;
        HashSet<string> seenOptions = new(StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < args.Length; index++)
        {
            var option = args[index];
            if (IsHelp(option))
            {
                showHelp = true;
                return false;
            }
            if (!seenOptions.Add(option))
            {
                error = $"Option '{option}' cannot be supplied more than once.";
                return false;
            }
            if (!TryReadValue(args, ref index, option, out var value, out error))
                return false;

            if (string.Equals(option, "--world", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryNormalizePath(value, option, out worldPath, out error))
                    return false;
                continue;
            }
            if (string.Equals(option, "--out", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryNormalizePath(value, option, out outputPath, out error))
                    return false;
                continue;
            }
            if (string.Equals(option, "--target", StringComparison.OrdinalIgnoreCase))
            {
                targetId = value;
                continue;
            }
            if (string.Equals(option, "--seed", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rootSeed))
                {
                    error = $"Seed '{value}' is not a signed 64-bit integer.";
                    return false;
                }
                rootSeedSpecified = true;
                continue;
            }
            if (string.Equals(option, "--batch-size", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out batchSize)
                    || batchSize <= 0)
                {
                    error = $"Batch size '{value}' is not a positive 32-bit integer.";
                    return false;
                }
                continue;
            }

            error = $"Unknown option '{option}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(worldPath))
        {
            error = "Missing required option '--world'.";
            return false;
        }
        if (!rootSeedSpecified)
        {
            error = "Missing required option '--seed'.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(targetId))
        {
            error = "Missing required option '--target'.";
            return false;
        }

        options = new(worldPath, outputPath, targetId, rootSeed, batchSize);
        return true;
    }

    static bool TryReadValue(
        string[] args,
        ref int index,
        string option,
        out string value,
        out string? error)
    {
        error = null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            error = $"Missing value for option '{option}'.";
            return false;
        }

        value = args[++index];
        return true;
    }

    static bool TryNormalizePath(
        string value,
        string option,
        out string path,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            path = string.Empty;
            error = $"Option '{option}' requires a non-empty path or '-'.";
            return false;
        }
        if (string.Equals(value, SimulationCliOptions.StandardStreamPath, StringComparison.Ordinal))
        {
            path = value;
            return true;
        }

        try
        {
            path = Path.GetFullPath(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            path = string.Empty;
            error = $"Option '{option}' has invalid path '{value}': {exception.Message}";
            return false;
        }
    }

    static bool IsHelp(string value) =>
        string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase);
}
