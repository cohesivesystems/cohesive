using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cohesive.Api;
using Cohesive.Identity;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Identity;

/// <summary>
/// Resolves requested scope selections from resource-bound API scope policies.
/// </summary>
public interface IApiScopeResourceSelectionResolver
{
    /// <summary>
    /// Returns whether this resolver can interpret the declared derivation metadata.
    /// </summary>
    bool CanResolve(ApiResourceScopeDerivation derivation);

    /// <summary>
    /// Attempts to derive a requested scope selection for a resource-bound policy.
    /// </summary>
    bool TryResolveRequestedScope(
        HttpContext httpContext,
        ApiScopePolicy policy,
        string scopeKind,
        out RequestedScopeSelection? selection
        );
}

/// <summary>
/// Structured resource id parsed from a resource-bound API parameter.
/// </summary>
public sealed record ApiStructuredResourceId
{
    /// <summary>
    /// Creates a parsed structured resource id.
    /// </summary>
    /// <param name="format">Resource id format.</param>
    /// <param name="fields">Parsed fields keyed by semantic field name.</param>
    public ApiStructuredResourceId(
        string format,
        IReadOnlyDictionary<string, string> fields
        )
    {
        Format = Guard.RequireNotNullOrWhiteSpace(format).Trim();
        Fields = fields.Count == 0
            ? ImmutableDictionary<string, string>.Empty
            : fields.ToImmutableDictionary(
                keySelector: static pair => Guard.RequireNotNullOrWhiteSpace(pair.Key).Trim(),
                elementSelector: static pair => Guard.RequireNotNullOrWhiteSpace(pair.Value).Trim(),
                keyComparer: StringComparer.Ordinal
                );
    }

    /// <summary>
    /// Resource id format.
    /// </summary>
    public string Format { get; }

    /// <summary>
    /// Parsed fields keyed by semantic field name.
    /// </summary>
    public IReadOnlyDictionary<string, string> Fields { get; }

    /// <summary>
    /// Attempts to read a parsed field by name.
    /// </summary>
    public bool TryGetField(
        string fieldName,
        [NotNullWhen(returnValue: true)] out string? value
        )
    {
        value = null;
        return !string.IsNullOrWhiteSpace(fieldName)
            && Fields.TryGetValue(fieldName.Trim(), out value)
            && !string.IsNullOrWhiteSpace(value);
    }
}

/// <summary>
/// Parses one structured resource id format into named fields.
/// </summary>
public interface IApiStructuredResourceIdParser
{
    /// <summary>
    /// Resource id format parsed by this parser.
    /// </summary>
    string Format { get; }

    /// <summary>
    /// Attempts to parse a structured resource id.
    /// </summary>
    bool TryParse(
        string resourceId,
        [NotNullWhen(returnValue: true)] out ApiStructuredResourceId? parsed
        );
}

sealed class StructuredResourceIdApiScopeResourceSelectionResolver(
    IEnumerable<IApiStructuredResourceIdParser> parsers
    ) : IApiScopeResourceSelectionResolver
{
    readonly ImmutableArray<IApiStructuredResourceIdParser> parserList = [..parsers];

    public bool CanResolve(ApiResourceScopeDerivation derivation)
    {
        ArgumentNullException.ThrowIfNull(derivation);
        return string.Equals(
                derivation.Strategy,
                ApiResourceScopeDerivationStrategies.StructuredResourceId,
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(derivation.Format)
            && !string.IsNullOrWhiteSpace(derivation.ScopeField)
            && TryGetParser(derivation.Format, out _);
    }

    public bool TryResolveRequestedScope(
        HttpContext httpContext,
        ApiScopePolicy policy,
        string scopeKind,
        out RequestedScopeSelection? selection
        )
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.ResourceDerivation is null
            || !CanResolve(policy.ResourceDerivation)
            || string.IsNullOrWhiteSpace(policy.ResourceParameterName)
            || string.IsNullOrWhiteSpace(policy.ResourceDerivation.Format)
            || string.IsNullOrWhiteSpace(policy.ResourceDerivation.ScopeField)
            || !httpContext.Request.RouteValues.TryGetValue(policy.ResourceParameterName, out var resourceValue)
            || resourceValue is null)
        {
            selection = null;
            return false;
        }

        var trimmed = resourceValue.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            || !TryGetParser(policy.ResourceDerivation.Format, out var parser)
            || !parser.TryParse(trimmed, out var parsed)
            || !parsed.TryGetField(policy.ResourceDerivation.ScopeField, out var scopeId))
        {
            selection = null;
            return false;
        }

        selection = new(
            ScopeKind: scopeKind,
            ScopeIds: [scopeId],
            Mode: ScopeSelectionMode.Single,
            Source: ScopeSelectionSource.RequestRoute
            );
        return true;
    }

    bool TryGetParser(
        string format,
        [NotNullWhen(returnValue: true)] out IApiStructuredResourceIdParser? parser
        )
    {
        parser = null;
        if (string.IsNullOrWhiteSpace(format))
            return false;

        var normalized = format.Trim();
        for (var i = 0; i < parserList.Length; i++)
        {
            if (string.Equals(parserList[i].Format, normalized, StringComparison.Ordinal))
            {
                parser = parserList[i];
                return true;
            }
        }

        return false;
    }
}

sealed class ScopedProcessInstanceIdStructuredResourceIdParser : IApiStructuredResourceIdParser
{
    const string ProcessTypeField = "processType";
    const string SuffixField = "suffix";

    public string Format => ApiResourceIdFormats.ScopedProcessInstanceId;

    public bool TryParse(
        string resourceId,
        [NotNullWhen(returnValue: true)] out ApiStructuredResourceId? parsed
        )
    {
        parsed = null;
        if (!ScopedProcessInstanceId.TryParse(resourceId, out var processId))
            return false;

        parsed = new(
            Format,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProcessTypeField] = processId.ProcessType,
                [ApiResourceScopeFields.ScopeId] = processId.ScopeId,
                [SuffixField] = processId.Suffix
            });
        return true;
    }
}
