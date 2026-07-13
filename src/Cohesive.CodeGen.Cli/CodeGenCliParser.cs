using System.Collections.Immutable;
using Cohesive.Adapters.TypeScript;

namespace Cohesive.CodeGen.Cli;

/// <summary>
/// Minimal command-line parser for the codegen CLI.
/// </summary>
public static class CodeGenCliParser
{
    /// <summary>
    /// Parses command-line arguments.
    /// </summary>
    public static bool TryParse(string[] args, out CodeGenCliOptions? options, out string? error, out bool showHelp)
    {
        options = null;
        error = null;
        showHelp = false;

        if (args.Length == 0)
        {
            showHelp = true;
            return false;
        }

        string? contracts = null;
        string? output = null;
        string? module = null;
        var shapeProjection = ContractShapeProjection.Clr;
        var emitKinds = ImmutableArray.CreateBuilder<CodeGenEmitKind>();
        var externalShapeModules = ImmutableArray.CreateBuilder<TypeScriptExternalTypeModule>();

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            if (string.Equals(current, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current, "-h", StringComparison.OrdinalIgnoreCase))
            {
                showHelp = true;
                return false;
            }

            if (!TryReadValue(args, ref i, current, out var value, out error))
                return false;

            if (string.Equals(current, "--contracts", StringComparison.OrdinalIgnoreCase))
            {
                contracts = value;
                continue;
            }

            if (string.Equals(current, "--out", StringComparison.OrdinalIgnoreCase))
            {
                output = value;
                continue;
            }

            if (string.Equals(current, "--module", StringComparison.OrdinalIgnoreCase))
            {
                module = value;
                continue;
            }

            if (string.Equals(current, "--emit", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseEmitKinds(value, emitKinds, out error))
                    return false;

                continue;
            }

            if (string.Equals(current, "--external-shapes", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseExternalShapeModule(value, externalShapeModules, out error))
                    return false;

                continue;
            }

            if (string.Equals(current, "--shape-projection", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseShapeProjection(value, out shapeProjection))
                {
                    error = $"Unsupported shape projection '{value}'.";
                    return false;
                }

                continue;
            }

            error = $"Unknown option '{current}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(contracts))
        {
            error = "Missing required option '--contracts'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            error = "Missing required option '--out'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(module))
        {
            error = "Missing required option '--module'.";
            return false;
        }

        if (emitKinds.Count == 0)
        {
            error = "Missing required option '--emit'.";
            return false;
        }

        options = new CodeGenCliOptions
        {
            ContractsAssemblyPath = Path.GetFullPath(contracts),
            OutputDirectory = Path.GetFullPath(output),
            ModuleName = module,
            EmitKinds = emitKinds.ToImmutable(),
            ExternalTypeScriptShapeModules = externalShapeModules.ToImmutable(),
            ShapeProjection = shapeProjection
        };

        return true;
    }

    static bool TryReadValue(string[] args, ref int index, string option, out string value, out string? error)
    {
        error = null;
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            error = $"Missing value for option '{option}'.";
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    static bool TryParseExternalShapeModule(
        string value,
        ImmutableArray<TypeScriptExternalTypeModule>.Builder externalShapeModules,
        out string? error)
    {
        error = null;
        var separatorIndex = value.IndexOf('=');
        if (separatorIndex <= 0 || separatorIndex + 1 >= value.Length)
        {
            error = "External shape module bindings must use '<clr-namespace-prefix>=<typescript-import-path>'.";
            return false;
        }

        var clrNamespacePrefix = value[..separatorIndex].Trim();
        var importPath = value[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(clrNamespacePrefix) || string.IsNullOrWhiteSpace(importPath))
        {
            error = "External shape module bindings require a CLR namespace prefix and TypeScript import path.";
            return false;
        }

        externalShapeModules.Add(new TypeScriptExternalTypeModule
        {
            TypeIdPrefix = ToClrTypeIdPrefix(clrNamespacePrefix),
            ShapeIdPrefix = ToClrShapeIdPrefix(clrNamespacePrefix),
            ImportPath = importPath
        });
        return true;
    }

    static string ToClrTypeIdPrefix(string clrNamespacePrefix) =>
        clrNamespacePrefix.StartsWith("clr:type:", StringComparison.Ordinal)
            ? clrNamespacePrefix
            : $"clr:type:{EnsurePrefixTerminator(clrNamespacePrefix)}";

    static string ToClrShapeIdPrefix(string clrNamespacePrefix) =>
        clrNamespacePrefix.StartsWith("clr:shape:", StringComparison.Ordinal)
            ? clrNamespacePrefix
            : $"clr:shape:{EnsurePrefixTerminator(clrNamespacePrefix)}";

    static string EnsurePrefixTerminator(string value) =>
        value.EndsWith(".", StringComparison.Ordinal) || value.EndsWith("+", StringComparison.Ordinal)
            ? value
            : value + ".";

    static bool TryParseEmitKinds(string value, ImmutableArray<CodeGenEmitKind>.Builder emitKinds, out string? error)
    {
        error = null;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseEmitKind(parts[i], out var emitKind))
            {
                error = $"Unsupported emit kind '{parts[i]}'.";
                return false;
            }

            if (!emitKinds.Contains(emitKind))
                emitKinds.Add(emitKind);
        }

        return true;
    }

    static bool TryParseEmitKind(string value, out CodeGenEmitKind emitKind)
    {
        if (string.Equals(value, "shapes", StringComparison.OrdinalIgnoreCase))
        {
            emitKind = CodeGenEmitKind.Shapes;
            return true;
        }

        if (string.Equals(value, "api", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "apis", StringComparison.OrdinalIgnoreCase))
        {
            emitKind = CodeGenEmitKind.Apis;
            return true;
        }

        if (string.Equals(value, "openapi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "open-api", StringComparison.OrdinalIgnoreCase))
        {
            emitKind = CodeGenEmitKind.OpenApi;
            return true;
        }

        if (string.Equals(value, "api-playwright", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "playwright-api", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "api-ui-mock", StringComparison.OrdinalIgnoreCase))
        {
            emitKind = CodeGenEmitKind.ApiPlaywright;
            return true;
        }

        if (string.Equals(value, "graphql", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "graph-ql", StringComparison.OrdinalIgnoreCase))
        {
            emitKind = CodeGenEmitKind.GraphQL;
            return true;
        }

        if (string.Equals(value, "constants", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "const", StringComparison.OrdinalIgnoreCase))
        {
            emitKind = CodeGenEmitKind.Constants;
            return true;
        }

        if (string.Equals(value, "transitions", StringComparison.OrdinalIgnoreCase))
        {
            emitKind = CodeGenEmitKind.Transitions;
            return true;
        }

        if (string.Equals(value, "processes", StringComparison.OrdinalIgnoreCase))
        {
            emitKind = CodeGenEmitKind.Processes;
            return true;
        }

        if (string.Equals(value, "invariants", StringComparison.OrdinalIgnoreCase))
        {
            emitKind = CodeGenEmitKind.Invariants;
            return true;
        }

        emitKind = default;
        return false;
    }

    static bool TryParseShapeProjection(string value, out ContractShapeProjection projection)
    {
        if (string.Equals(value, "clr", StringComparison.OrdinalIgnoreCase))
        {
            projection = ContractShapeProjection.Clr;
            return true;
        }

        if (string.Equals(value, "canonical-json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
        {
            projection = ContractShapeProjection.CanonicalJson;
            return true;
        }

        projection = default;
        return false;
    }
}
