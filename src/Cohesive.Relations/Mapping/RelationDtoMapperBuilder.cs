using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Mapping;

static class RelationDtoMapperBuilder
{
    static readonly MethodInfo ReadMethod = typeof(RelationDtoValueReader)
        .GetMethod(nameof(RelationDtoValueReader.Read), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DTO value reader method is unavailable.");

    internal static RelationDtoMapperCompilationResult<TOutput> Compile<TOutput>(
        CompiledRelationQueryPlan plan,
        RelationDtoMapperProfile profile,
        RelationDtoMapperCompilationOptions options
        )
    {
        var diagnostics = ImmutableArray.CreateBuilder<RelationDtoMapperDiagnostic>();
        var outputType = typeof(TOutput);
        var planReference = RelationQueryCompiledPlanReference.From(plan);
        var compilation = new RelationDtoMapperCompilationDescriptor(
            planReference: planReference,
            outputType: outputType,
            profileId: profile.Id,
            profileFingerprint: profile.Fingerprint,
            optionsFingerprint: options.Fingerprint,
            compilationIdentity: RelationDtoMapperFingerprint.ComputeCompilation(
                planReference,
                outputType,
                profile.Fingerprint,
                options.Fingerprint
                )
            );
        var terminal = plan.ExecutionSlice.RelationOutput;
        if (terminal is null)
        {
            diagnostics.Add(Diagnostic(
                RelationDtoMapperDiagnosticCodes.UnsupportedTerminal,
                "DTO mapper compilation requires a canonical relation terminal; named query results are not supported by v1."));
            return Failed<TOutput>(compilation, diagnostics);
        }

        var outputShapeId = terminal.Definition.Shape;
        var graphDocument = plan.Provenance.ShapeDocuments.SingleOrDefault(document => document.Graph.Id == outputShapeId.GraphId);
        var shape = graphDocument?.Graph.TryGetShape(outputShapeId.ShapeId);
        if (shape is null)
        {
            diagnostics.Add(Diagnostic(
                RelationDtoMapperDiagnosticCodes.OutputShapeUnavailable,
                "The relation output shape is absent from the exact compiled-plan snapshots.",
                terminal.Relation,
                outputShapeId));
            return Failed<TOutput>(compilation, diagnostics);
        }

        if (!IsSupportedTarget(outputType))
        {
            diagnostics.Add(Diagnostic(
                RelationDtoMapperDiagnosticCodes.UnsupportedTargetType,
                $"CLR target type '{Display(outputType)}' is not a concrete object or record DTO type supported by v1.",
                terminal.Relation,
                outputShapeId));
            return Failed<TOutput>(compilation, diagnostics);
        }

        NullabilityInfoContext nullability = new();
        var properties = GetProperties(outputType, nullability);
        if (properties.IsDefaultOrEmpty)
        {
            diagnostics.Add(Diagnostic(
                RelationDtoMapperDiagnosticCodes.UnsupportedTargetType,
                $"CLR target type '{Display(outputType)}' exposes no readable public instance properties.",
                terminal.Relation,
                outputShapeId));
            return Failed<TOutput>(compilation, diagnostics);
        }

        if (terminal.Fields.IsDefaultOrEmpty)
        {
            diagnostics.Add(Diagnostic(
                RelationDtoMapperDiagnosticCodes.UnsupportedTerminal,
                "The compiled relation demand exposes no top-level output fields for DTO materialization.",
                terminal.Relation,
                outputShapeId));
            return Failed<TOutput>(compilation, diagnostics);
        }

        var demandedPaths = terminal.Fields.Select(static field => field.Path).ToHashSet();
        foreach (var explicitBinding in profile.Bindings)
        {
            if (demandedPaths.Contains(explicitBinding.OutputField))
                continue;

            diagnostics.Add(Diagnostic(
                RelationDtoMapperDiagnosticCodes.OutputFieldUnmapped,
                $"Explicit binding field '{explicitBinding.OutputField}' is not demanded by the compiled relation terminal.",
                terminal.Relation,
                outputShapeId,
                explicitBinding.OutputField,
                explicitBinding.TargetMember));
        }

        List<PreliminaryBinding> preliminary = new(terminal.Fields.Length);
        foreach (var fieldReference in terminal.Fields)
        {
            if (fieldReference.Path.Segments.Length != 1
                || !fieldReference.Path.Segments[0].TryGetFieldIdentity(out var fieldName))
            {
                diagnostics.Add(Diagnostic(
                    RelationDtoMapperDiagnosticCodes.UnsupportedConversion,
                    $"Nested or structural output field '{fieldReference.Path}' is outside the v1 top-level scalar mapper surface.",
                    terminal.Relation,
                    outputShapeId,
                    fieldReference.Path));
                continue;
            }

            var fieldDefinitions = shape.Fields
                .Where(field => string.Equals(field.Name.Value, fieldName, StringComparison.Ordinal))
                .ToArray();
            
            if (fieldDefinitions.Length != 1)
            {
                diagnostics.Add(Diagnostic(
                    RelationDtoMapperDiagnosticCodes.OutputShapeUnavailable,
                    $"Demanded output field '{fieldReference.Path}' cannot be resolved uniquely in the persisted shape snapshot.",
                    terminal.Relation,
                    outputShapeId,
                    fieldReference.Path));
                continue;
            }

            if (!TrySelectProperty(
                    field: fieldReference.Path,
                    fieldName: fieldName,
                    properties,
                    profile,
                    out var property,
                    out var bindingSource,
                    out var selectionMessage))
            {
                diagnostics.Add(Diagnostic(
                    selectionMessage.IsAmbiguous
                        ? RelationDtoMapperDiagnosticCodes.AmbiguousMemberBinding
                        : RelationDtoMapperDiagnosticCodes.OutputFieldUnmapped,
                    selectionMessage.Message,
                    terminal.Relation,
                    outputShapeId,
                    fieldReference.Path,
                    selectionMessage.TargetMember));
                continue;
            }

            var outputReference = terminal.Outputs.FirstOrDefault(output =>
                output.Field is { } outputField && outputField == fieldReference);
            var assignment = FindAssignment(plan, terminal.Definition.Node, fieldReference.Path);
            preliminary.Add(new(
                fieldReference,
                fieldDefinitions[0],
                property,
                bindingSource,
                outputReference,
                assignment
            ));
        }

        foreach (var duplicate in preliminary.GroupBy(static binding => binding.Property).Where(static group => group.Count() > 1))
        {
            diagnostics.Add(Diagnostic(
                RelationDtoMapperDiagnosticCodes.AmbiguousMemberBinding,
                $"CLR member '{duplicate.Key.Name}' is selected by more than one demanded output field.",
                terminal.Relation,
                outputShapeId,
                targetMember: duplicate.Key.Name
                ));
        }

        var mappedProperties = preliminary.Select(static binding => binding.Property).ToHashSet();
        var publicConstructors = outputType.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        foreach (var property in properties)
        {
            if (mappedProperties.Contains(property.Property))
                continue;
            
            var requiredByEveryConstructor = publicConstructors.Length > 0
                && publicConstructors.All(constructor => constructor.GetParameters().Any(parameter =>
                    !parameter.HasDefaultValue
                    && string.Equals(
                        parameter.Name,
                        property.Property.Name,
                        StringComparison.OrdinalIgnoreCase)));
            if (!property.IsRequired
                && !requiredByEveryConstructor
                && (property.HasPublicSetter || property.AllowsNull))
                continue;

            diagnostics.Add(Diagnostic(
                RelationDtoMapperDiagnosticCodes.RequiredTargetMemberUnmapped,
                $"Required CLR target member '{property.Property.Name}' has no demanded relation output binding.",
                terminal.Relation,
                outputShapeId,
                targetMember: property.Property.Name));
        }

        if (HasErrors(diagnostics))
            return Failed<TOutput>(compilation, diagnostics);

        if (!TrySelectConstructor(
                outputType,
                properties,
                mappedProperties,
                out var constructor,
                out var parameterProperties,
                out var constructorMessage))
        {
            diagnostics.Add(Diagnostic(
                RelationDtoMapperDiagnosticCodes.ConstructorUnavailable,
                constructorMessage,
                terminal.Relation,
                outputShapeId));
            return Failed<TOutput>(compilation, diagnostics);
        }

        var constructorParameters = constructor?.GetParameters() ?? [];
        Dictionary<PropertyInfo, ParameterInfo> parametersByProperty = [];
        foreach (var pair in parameterProperties)
            parametersByProperty.Add(pair.Value, pair.Key);

        List<RelationDtoCompiledBinding> compiledBindings = new(preliminary.Count);
        foreach (var binding in preliminary)
        {
            var propertyMetadata = properties.Single(candidate => candidate.Property == binding.Property);
            var hasParameter = parametersByProperty.TryGetValue(binding.Property, out var parameter);
            if (!hasParameter && !propertyMetadata.HasPublicSetter)
            {
                diagnostics.Add(Diagnostic(
                    RelationDtoMapperDiagnosticCodes.ConstructorUnavailable,
                    $"CLR target member '{binding.Property.Name}' is neither constructor-bound nor publicly writable.",
                    terminal.Relation,
                    outputShapeId,
                    binding.Field.Path,
                    binding.Property.Name,
                    binding.OutputReference?.Node,
                    binding.Assignment));
                continue;
            }

            var destinationType = parameter?.ParameterType ?? binding.Property.PropertyType;
            var allowsNull = parameter is not null
                ? AllowsNull(destinationType, nullability.Create(parameter).ReadState)
                : propertyMetadata.AllowsNull;
            var hasMissingDefault = parameter?.HasDefaultValue == true;
            var missingDefault = hasMissingDefault ? NormalizeDefault(parameter!, destinationType) : null;
            var acceptsObservationAbsence = Nullable.GetUnderlyingType(destinationType) == typeof(ObservationValue)
                                             || destinationType == typeof(ObservationValue);

            if (binding.Definition.Cardinality != FieldCardinality.Single)
            {
                diagnostics.Add(ConversionDiagnostic(
                    binding,
                    terminal,
                    RelationDtoMapperDiagnosticCodes.UnsupportedConversion,
                    "Collection-valued semantic fields are outside the v1 scalar DTO mapper surface."));
                continue;
            }

            if (binding.Definition.Nullability == FieldNullability.Nullable
                && !allowsNull
                && !acceptsObservationAbsence)
            {
                diagnostics.Add(ConversionDiagnostic(
                    binding,
                    terminal,
                    RelationDtoMapperDiagnosticCodes.PresenceOrNullabilityMismatch,
                    $"Nullable semantic field '{binding.Field.Path}' cannot satisfy non-nullable CLR member '{binding.Property.Name}'."));
                continue;
            }

            if (binding.Definition.Presence == FieldPresence.Optional
                && !allowsNull
                && !hasMissingDefault
                && !acceptsObservationAbsence)
            {
                diagnostics.Add(ConversionDiagnostic(
                    binding,
                    terminal,
                    RelationDtoMapperDiagnosticCodes.PresenceOrNullabilityMismatch,
                    $"Optional semantic field '{binding.Field.Path}' has no nullable or defaulted CLR destination."));
                continue;
            }

            if (!TryGetConversion(
                    binding.Definition.Type,
                    destinationType,
                    options.NumericConversions,
                    out var conversion))
            {
                diagnostics.Add(ConversionDiagnostic(
                    binding,
                    terminal,
                    RelationDtoMapperDiagnosticCodes.UnsupportedConversion,
                    $"Semantic field '{binding.Field.Path}' cannot be converted to CLR type '{Display(destinationType)}' under the selected options."));
                continue;
            }

            compiledBindings.Add(new(
                binding.Field,
                binding.Definition,
                binding.Property.Name,
                destinationType,
                binding.BindingSource,
                binding.OutputReference,
                binding.Assignment,
                conversion,
                allowsNull || acceptsObservationAbsence,
                binding.Definition.Presence == FieldPresence.Optional
                    && (allowsNull || hasMissingDefault || acceptsObservationAbsence),
                acceptsObservationAbsence
                    ? ObservationValue.Undefined
                    : hasMissingDefault
                        ? missingDefault
                        : null));
        }

        if (HasErrors(diagnostics))
            return Failed<TOutput>(compilation, diagnostics);

        Func<ObservationValue, TOutput> kernel;
        try
        {
            kernel = CompileKernel<TOutput>(
                constructor,
                constructorParameters,
                parameterProperties,
                preliminary,
                compiledBindings
                );
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                          and not StackOverflowException
                                          and not AccessViolationException)
        {
            diagnostics.Add(Diagnostic(
                RelationDtoMapperDiagnosticCodes.UnsupportedTargetType,
                $"CLR DTO kernel generation failed with '{exception.GetType().Name}'.",
                terminal.Relation,
                outputShapeId));
            return Failed<TOutput>(compilation, diagnostics);
        }

        var memberDescriptors = compiledBindings
            .OrderBy(static binding => binding.OutputField.Path.ToString(), StringComparer.Ordinal)
            .Select(static binding => new RelationDtoMapperMemberDescriptor(
                binding.OutputField,
                binding.TargetMember,
                binding.TargetType,
                binding.BindingSource,
                binding.OutputReference
                )
            )
            .ToImmutableArray();
        var descriptor = new RelationDtoMapperDescriptor(
            compilation,
            terminal.Relation,
            terminal.Definition.Shape,
            terminal.Definition.Mode,
            memberDescriptors
            );
        return new(compilation, new(descriptor, kernel), Sort(diagnostics));
    }

    static Func<ObservationValue, TOutput> CompileKernel<TOutput>(
        ConstructorInfo? constructor,
        IReadOnlyList<ParameterInfo> constructorParameters,
        IReadOnlyDictionary<ParameterInfo, PropertyInfo> parameterProperties,
        IReadOnlyList<PreliminaryBinding> preliminary,
        IReadOnlyList<RelationDtoCompiledBinding> compiledBindings
        )
    {
        var value = Expression.Parameter(typeof(ObservationValue), "value");
        var bindingByProperty = preliminary
            .Zip(compiledBindings, static (source, compiled) => (source.Property, Compiled: compiled))
            .ToDictionary(static pair => pair.Property, static pair => pair.Compiled);

        NewExpression created;
        if (constructor is null)
        {
            created = Expression.New(typeof(TOutput));
        }
        else
        {
            var arguments = new Expression[constructorParameters.Count];
            for (var index = 0; index < constructorParameters.Count; index++)
            {
                var parameter = constructorParameters[index];
                if (parameterProperties.TryGetValue(parameter, out var property)
                    && bindingByProperty.TryGetValue(property, out var binding))
                {
                    arguments[index] = Read(value, binding, parameter.ParameterType);
                }
                else
                {
                    arguments[index] = Expression.Constant(
                        NormalizeDefault(parameter, parameter.ParameterType),
                        parameter.ParameterType
                        );
                }
            }
            created = Expression.New(constructor, arguments);
        }

        var constructorBoundProperties = parameterProperties.Values.ToHashSet();
        var initializers = preliminary
            .Where(binding => !constructorBoundProperties.Contains(binding.Property))
            .Select(binding => Expression.Bind(
                binding.Property,
                Read(value, bindingByProperty[binding.Property], binding.Property.PropertyType)))
            .ToArray();
        Expression body = initializers.Length == 0
            ? created
            : Expression.MemberInit(created, initializers);
        return Expression.Lambda<Func<ObservationValue, TOutput>>(body, value).Compile();
    }

    static MethodCallExpression Read(
        ParameterExpression value,
        RelationDtoCompiledBinding binding,
        Type destinationType
        ) =>
        Expression.Call(
            ReadMethod.MakeGenericMethod(
                destinationType,
                GetConverterType(binding.Conversion, destinationType)),
            value,
            Expression.Constant(binding));

    static Type GetConverterType(RelationDtoValueConversion conversion, Type destinationType)
    {
        var converter = conversion switch
        {
            RelationDtoValueConversion.ObservationValue => typeof(RelationDtoValueReader.ObservationConverter<>),
            RelationDtoValueConversion.Boolean => typeof(RelationDtoValueReader.BooleanConverter<>),
            RelationDtoValueConversion.Int32 => typeof(RelationDtoValueReader.Int32Converter<>),
            RelationDtoValueConversion.Int64 => typeof(RelationDtoValueReader.Int64Converter<>),
            RelationDtoValueConversion.Decimal => typeof(RelationDtoValueReader.DecimalConverter<>),
            RelationDtoValueConversion.String => typeof(RelationDtoValueReader.StringConverter<>),
            RelationDtoValueConversion.Guid => typeof(RelationDtoValueReader.GuidConverter<>),
            RelationDtoValueConversion.Date => typeof(RelationDtoValueReader.DateConverter<>),
            RelationDtoValueConversion.DateTime => typeof(RelationDtoValueReader.DateTimeConverter<>),
            RelationDtoValueConversion.Instant => typeof(RelationDtoValueReader.InstantConverter<>),
            RelationDtoValueConversion.ReadOnlyBytes => typeof(RelationDtoValueReader.ReadOnlyBytesConverter<>),
            RelationDtoValueConversion.ByteArray => typeof(RelationDtoValueReader.ByteArrayConverter<>),
            RelationDtoValueConversion.Enum => typeof(RelationDtoValueReader.EnumConverter<>),
            RelationDtoValueConversion.Int32ToInt64 => typeof(RelationDtoValueReader.Int32ToInt64Converter<>),
            RelationDtoValueConversion.Int32ToDecimal => typeof(RelationDtoValueReader.Int32ToDecimalConverter<>),
            RelationDtoValueConversion.Int64ToDecimal => typeof(RelationDtoValueReader.Int64ToDecimalConverter<>),
            RelationDtoValueConversion.GuidString => typeof(RelationDtoValueReader.GuidStringConverter<>),
            RelationDtoValueConversion.EnumString => typeof(RelationDtoValueReader.EnumStringConverter<>),
            RelationDtoValueConversion.EntityReferenceString =>
                typeof(RelationDtoValueReader.EntityReferenceStringConverter<>),
            _ => throw new InvalidOperationException("Unsupported compiled DTO conversion.")
        };
        return converter.MakeGenericType(destinationType);
    }

    static ImmutableArray<TargetProperty> GetProperties(
        Type type,
        NullabilityInfoContext nullability) =>
    [
        .. type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.GetMethod?.IsPublic == true
                                      && property.GetIndexParameters().Length == 0)
            .Select(property => new TargetProperty(
                property,
                property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name,
                AllowsNull(property.PropertyType, nullability.Create(property).ReadState),
                property.GetCustomAttribute<RequiredMemberAttribute>(inherit: true) is not null,
                property.SetMethod?.IsPublic == true))
            .OrderBy(static property => property.Property.Name, StringComparer.Ordinal)
    ];

    static bool TrySelectProperty(
        FieldPath field,
        string fieldName,
        ImmutableArray<TargetProperty> properties,
        RelationDtoMapperProfile profile,
        out PropertyInfo property,
        out RelationDtoMemberBindingSource bindingSource,
        out PropertySelectionFailure failure)
    {
        var explicitBinding = profile.Bindings.FirstOrDefault(binding => binding.OutputField == field);
        if (explicitBinding is not null)
        {
            var matches = properties
                .Where(candidate => string.Equals(
                    candidate.Property.Name,
                    explicitBinding.TargetMember,
                    StringComparison.Ordinal
                )).ToArray();
            if (matches.Length == 1)
            {
                property = matches[0].Property;
                bindingSource = RelationDtoMemberBindingSource.Explicit;
                failure = default;
                return true;
            }

            property = null!;
            bindingSource = default;
            failure = new(
                matches.Length > 1,
                matches.Length > 1
                    ? $"Explicit target member '{explicitBinding.TargetMember}' is ambiguous on the CLR target type."
                    : $"Explicit target member '{explicitBinding.TargetMember}' does not exist as a readable public property.",
                explicitBinding.TargetMember);
            return false;
        }

        if (profile.MemberConvention == RelationDtoMemberConvention.SerializedNameThenExactMemberName)
        {
            var serializedMatches = properties
                .Where(candidate => string.Equals(candidate.SerializedName, fieldName, StringComparison.Ordinal))
                .ToArray();
            if (serializedMatches.Length == 1)
            {
                property = serializedMatches[0].Property;
                bindingSource = RelationDtoMemberBindingSource.SerializedName;
                failure = default;
                return true;
            }
            if (serializedMatches.Length > 1)
            {
                property = null!;
                bindingSource = default;
                failure = new(
                    true,
                    $"Serialized name '{fieldName}' matches more than one CLR property.",
                    TargetMember: null);
                return false;
            }
        }

        if (profile.MemberConvention is RelationDtoMemberConvention.ExactMemberName
            or RelationDtoMemberConvention.SerializedNameThenExactMemberName)
        {
            var exactMatches = properties
                .Where(candidate => string.Equals(candidate.Property.Name, fieldName, StringComparison.Ordinal))
                .ToArray();
            if (exactMatches.Length == 1)
            {
                property = exactMatches[0].Property;
                bindingSource = RelationDtoMemberBindingSource.ExactMemberName;
                failure = default;
                return true;
            }
            if (exactMatches.Length > 1)
            {
                property = null!;
                bindingSource = default;
                failure = new(true, $"Exact CLR member name '{fieldName}' is ambiguous.", fieldName);
                return false;
            }
        }

        property = null!;
        bindingSource = default;
        failure = new(
            false,
            $"Demanded output field '{field}' has no CLR target member under profile '{profile.Id}'.",
            TargetMember: null);
        return false;
    }

    static bool TrySelectConstructor(
        Type outputType,
        ImmutableArray<TargetProperty> properties,
        IReadOnlySet<PropertyInfo> mappedProperties,
        out ConstructorInfo? constructor,
        out IReadOnlyDictionary<ParameterInfo, PropertyInfo> parameterProperties,
        out string message)
    {
        var constructors = outputType.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        var attributed = constructors
            .Where(static candidate => candidate.GetCustomAttribute<JsonConstructorAttribute>() is not null)
            .ToArray();
        if (attributed.Length > 1)
        {
            constructor = null;
            parameterProperties = ImmutableDictionary<ParameterInfo, PropertyInfo>.Empty;
            message = "More than one public constructor declares JsonConstructorAttribute.";
            return false;
        }

        List<ConstructorCandidate> viable = [];
        foreach (var candidate in constructors)
        {
            if (TryAssociateParameters(candidate, properties, mappedProperties, out var associated))
                viable.Add(new(candidate, associated));
        }

        if (attributed.Length == 1)
        {
            var selected = viable.SingleOrDefault(candidate => candidate.Constructor == attributed[0]);
            if (selected is null)
            {
                constructor = null;
                parameterProperties = ImmutableDictionary<ParameterInfo, PropertyInfo>.Empty;
                message = "The JsonConstructorAttribute constructor requires a parameter without a mapped CLR property.";
                return false;
            }

            constructor = selected.Constructor;
            parameterProperties = selected.ParameterProperties;
            message = string.Empty;
            return true;
        }

        var maximumArity = viable.Count == 0 ? -1 : viable.Max(static candidate => candidate.Constructor.GetParameters().Length);
        var best = viable
            .Where(candidate => candidate.Constructor.GetParameters().Length == maximumArity)
            .ToArray();
        if (best.Length > 1)
        {
            constructor = null;
            parameterProperties = ImmutableDictionary<ParameterInfo, PropertyInfo>.Empty;
            message = $"More than one viable public constructor has the maximum arity of {maximumArity}.";
            return false;
        }

        if (best.Length == 1)
        {
            constructor = best[0].Constructor;
            parameterProperties = best[0].ParameterProperties;
            message = string.Empty;
            return true;
        }

        if (outputType.IsValueType)
        {
            constructor = null;
            parameterProperties = ImmutableDictionary<ParameterInfo, PropertyInfo>.Empty;
            message = string.Empty;
            return true;
        }

        constructor = null;
        parameterProperties = ImmutableDictionary<ParameterInfo, PropertyInfo>.Empty;
        message = "No public constructor can be satisfied by the demanded relation output fields.";
        return false;
    }

    static bool TryAssociateParameters(
        ConstructorInfo constructor,
        ImmutableArray<TargetProperty> properties,
        IReadOnlySet<PropertyInfo> mappedProperties,
        out IReadOnlyDictionary<ParameterInfo, PropertyInfo> associated)
    {
        Dictionary<ParameterInfo, PropertyInfo> result = [];
        HashSet<PropertyInfo> seen = [];
        foreach (var parameter in constructor.GetParameters())
        {
            var matches = properties
                .Where(candidate => string.Equals(
                    candidate.Property.Name,
                    parameter.Name,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 1
                && mappedProperties.Contains(matches[0].Property)
                && seen.Add(matches[0].Property))
            {
                result.Add(parameter, matches[0].Property);
                continue;
            }

            if (parameter.HasDefaultValue)
                continue;

            associated = ImmutableDictionary<ParameterInfo, PropertyInfo>.Empty;
            return false;
        }

        associated = result;
        return true;
    }

    static bool TryGetConversion(
        TypeRef semanticType,
        Type destinationType,
        RelationDtoNumericConversionPolicy numericPolicy,
        out RelationDtoValueConversion conversion)
    {
        var target = Nullable.GetUnderlyingType(destinationType) ?? destinationType;
        if (target == typeof(ObservationValue)
            && semanticType is ScalarTypeRef or EnumTypeRef or EntityReferenceTypeRef)
        {
            conversion = RelationDtoValueConversion.ObservationValue;
            return true;
        }

        switch (semanticType)
        {
            case ScalarTypeRef { Kind: ScalarTypeKind.Bool } when target == typeof(bool):
                conversion = RelationDtoValueConversion.Boolean;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Int32 } when target == typeof(int):
                conversion = RelationDtoValueConversion.Int32;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Int32 }
                when numericPolicy == RelationDtoNumericConversionPolicy.LosslessWidening && target == typeof(long):
                conversion = RelationDtoValueConversion.Int32ToInt64;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Int32 }
                when numericPolicy == RelationDtoNumericConversionPolicy.LosslessWidening && target == typeof(decimal):
                conversion = RelationDtoValueConversion.Int32ToDecimal;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Int64 }
                when numericPolicy == RelationDtoNumericConversionPolicy.LosslessWidening && target == typeof(decimal):
                conversion = RelationDtoValueConversion.Int64ToDecimal;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Int64 } when target == typeof(long):
                conversion = RelationDtoValueConversion.Int64;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Decimal } when target == typeof(decimal):
                conversion = RelationDtoValueConversion.Decimal;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.String } when target == typeof(string):
                conversion = RelationDtoValueConversion.String;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Guid } when target == typeof(Guid):
                conversion = RelationDtoValueConversion.Guid;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Guid } when target == typeof(string):
                conversion = RelationDtoValueConversion.GuidString;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Date } when target == typeof(DateOnly):
                conversion = RelationDtoValueConversion.Date;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.DateTime } when target == typeof(DateTime):
                conversion = RelationDtoValueConversion.DateTime;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Instant } when target == typeof(DateTimeOffset):
                conversion = RelationDtoValueConversion.Instant;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Bytes } when target == typeof(ReadOnlyMemory<byte>):
                conversion = RelationDtoValueConversion.ReadOnlyBytes;
                return true;
            case ScalarTypeRef { Kind: ScalarTypeKind.Bytes } when target == typeof(byte[]):
                conversion = RelationDtoValueConversion.ByteArray;
                return true;
            case EntityReferenceTypeRef when target == typeof(string):
                conversion = RelationDtoValueConversion.EntityReferenceString;
                return true;
            case EntityReferenceTypeRef when target == typeof(Guid):
                conversion = RelationDtoValueConversion.Guid;
                return true;
            case EnumTypeRef enumeration when target == typeof(string):
                conversion = RelationDtoValueConversion.EnumString;
                return true;
            case EnumTypeRef enumeration when target.IsEnum
                                                  && enumeration.Members.All(member => Enum.IsDefined(target, member)):
                conversion = RelationDtoValueConversion.Enum;
                return true;
            default:
                conversion = default;
                return false;
        }
    }

    static bool IsSupportedTarget(Type type) =>
        !type.IsAbstract
        && !type.IsInterface
        && !type.ContainsGenericParameters
        && !type.IsArray
        && !type.IsPointer
        && !type.IsByRef
        && !type.IsPrimitive
        && !type.IsEnum
        && type != typeof(string)
        && type != typeof(decimal)
        && type != typeof(DateOnly)
        && type != typeof(DateTime)
        && type != typeof(DateTimeOffset)
        && type != typeof(Guid)
        && type != typeof(ObservationValue);

    static bool AllowsNull(Type type, NullabilityState state) =>
        Nullable.GetUnderlyingType(type) is not null
        || (!type.IsValueType && state == NullabilityState.Nullable);

    static object? NormalizeDefault(ParameterInfo parameter, Type destinationType)
    {
        if (!parameter.HasDefaultValue || parameter.DefaultValue is DBNull or Missing)
            return destinationType.IsValueType ? Activator.CreateInstance(destinationType) : null;
        return parameter.DefaultValue;
    }

    static QueryAssignmentId? FindAssignment(
        CompiledRelationQueryPlan plan,
        QueryNodeId node,
        FieldPath target) =>
        plan.ExecutionSlice.Nodes
            .Where(candidate => candidate.Id == node)
            .SelectMany(static candidate => candidate.ProjectionAssignments)
            .Where(assignment => assignment.Definition.Target == target)
            .Select(static assignment => (QueryAssignmentId?)assignment.Definition.Id)
            .SingleOrDefault();

    static RelationDtoMapperDiagnostic ConversionDiagnostic(
        PreliminaryBinding binding,
        RelationQueryRelationExecutionOutput terminal,
        string code,
        string message) =>
        Diagnostic(
            code,
            message,
            terminal.Relation,
            terminal.Definition.Shape,
            binding.Field.Path,
            binding.Property.Name,
            binding.OutputReference?.Node,
            binding.Assignment);

    static RelationDtoMapperDiagnostic Diagnostic(
        string code,
        string message,
        Relations.Model.RelationId? relation = null,
        QualifiedShapeId? shape = null,
        FieldPath? field = null,
        string? targetMember = null,
        QueryNodeId? node = null,
        QueryAssignmentId? assignment = null) =>
        new(code,
            DiagnosticSeverity.Error,
            RelationDtoMapperDiagnosticPhase.Compilation,
            message,
            relation,
            shape,
            field,
            targetMember,
            node,
            assignment);

    static RelationDtoMapperCompilationResult<TOutput> Failed<TOutput>(
        RelationDtoMapperCompilationDescriptor compilation,
        ImmutableArray<RelationDtoMapperDiagnostic>.Builder diagnostics) =>
        new(compilation, null, Sort(diagnostics));

    static ImmutableArray<RelationDtoMapperDiagnostic> Sort(
        ImmutableArray<RelationDtoMapperDiagnostic>.Builder diagnostics) =>
    [
        .. diagnostics.OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Field?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.TargetMember ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
    ];

    static bool HasErrors(ImmutableArray<RelationDtoMapperDiagnostic>.Builder diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    static string Display(Type type) => type.FullName ?? type.Name;

    sealed record TargetProperty(
        PropertyInfo Property,
        string? SerializedName,
        bool AllowsNull,
        bool IsRequired,
        bool HasPublicSetter);

    sealed record PreliminaryBinding(
        RelationQueryFieldReference Field,
        FieldDefinition Definition,
        PropertyInfo Property,
        RelationDtoMemberBindingSource BindingSource,
        RelationQueryOutputReference? OutputReference,
        QueryAssignmentId? Assignment);

    sealed record ConstructorCandidate(
        ConstructorInfo Constructor,
        IReadOnlyDictionary<ParameterInfo, PropertyInfo> ParameterProperties);

    readonly record struct PropertySelectionFailure(bool IsAmbiguous, string Message, string? TargetMember);
}

enum RelationDtoValueConversion
{
    ObservationValue = 0,
    Boolean = 1,
    Int32 = 2,
    Int64 = 3,
    Decimal = 4,
    String = 5,
    Guid = 6,
    Date = 7,
    DateTime = 8,
    Instant = 9,
    ReadOnlyBytes = 10,
    ByteArray = 11,
    Enum = 12,
    Int32ToInt64 = 13,
    Int32ToDecimal = 14,
    Int64ToDecimal = 15,
    GuidString = 16,
    EnumString = 17,
    EntityReferenceString = 18
}

sealed class RelationDtoCompiledBinding
{
    internal RelationDtoCompiledBinding(
        RelationQueryFieldReference outputField,
        FieldDefinition definition,
        string targetMember,
        Type targetType,
        RelationDtoMemberBindingSource bindingSource,
        RelationQueryOutputReference? outputReference,
        QueryAssignmentId? assignment,
        RelationDtoValueConversion conversion,
        bool allowsNull,
        bool allowsMissing,
        object? missingValue)
    {
        OutputField = outputField;
        Definition = definition;
        TargetMember = targetMember;
        TargetType = targetType;
        BindingSource = bindingSource;
        OutputReference = outputReference;
        Assignment = assignment;
        Conversion = conversion;
        AllowsNull = allowsNull;
        AllowsMissing = allowsMissing;
        MissingValue = missingValue;
    }

    internal RelationQueryFieldReference OutputField { get; }
    internal FieldDefinition Definition { get; }
    internal string TargetMember { get; }
    internal Type TargetType { get; }
    internal RelationDtoMemberBindingSource BindingSource { get; }
    internal RelationQueryOutputReference? OutputReference { get; }
    internal QueryAssignmentId? Assignment { get; }
    internal RelationDtoValueConversion Conversion { get; }
    internal bool AllowsNull { get; }
    internal bool AllowsMissing { get; }
    internal object? MissingValue { get; }
}

static class RelationDtoValueReader
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T Read<T, TConverter>(in ObservationValue root, RelationDtoCompiledBinding binding)
        where TConverter : struct, IConverter<T>
    {
        if (!root.TryGetProperty(binding.OutputField.Path.Segments[0].Segment!, out var value)
            || value.Kind == ObservationValueKind.Undefined)
        {
            if (binding.AllowsMissing)
                return Cast<T>(binding.MissingValue);
            throw new RelationDtoRowMappingException(
                binding,
                $"Required canonical field '{binding.OutputField.Path}' is missing.");
        }

        if (value.Kind == ObservationValueKind.Null)
        {
            if (binding.AllowsNull)
            {
                return binding.Conversion == RelationDtoValueConversion.ObservationValue
                    ? Value<T, ObservationValue>(ObservationValue.Null)
                    : default!;
            }
            throw new RelationDtoRowMappingException(
                binding,
                $"Canonical field '{binding.OutputField.Path}' is null but the CLR destination is non-nullable.");
        }

        try
        {
            return TConverter.Convert(in value, binding);
        }
        catch (RelationDtoRowMappingException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or FormatException
                                          or OverflowException
                                          or ArgumentException)
        {
            throw new RelationDtoRowMappingException(
                binding,
                $"Canonical field '{binding.OutputField.Path}' cannot be converted to CLR member '{binding.TargetMember}'.",
                exception);
        }
    }

    static ObservationValue RequireSemanticValue(
        RelationDtoCompiledBinding binding,
        ObservationValue value)
    {
        var valid = binding.Definition.Type switch
        {
            ScalarTypeRef { Kind: ScalarTypeKind.Bool } => value.TryGetBoolean(out _),
            ScalarTypeRef { Kind: ScalarTypeKind.Int32 } => value.TryGetInt32(out _),
            ScalarTypeRef { Kind: ScalarTypeKind.Int64 } => value.TryGetInt64(out _),
            ScalarTypeRef { Kind: ScalarTypeKind.Decimal } => value.TryGetDecimal(out _),
            ScalarTypeRef { Kind: ScalarTypeKind.String } => TryGetSemanticString(value, out _),
            ScalarTypeRef { Kind: ScalarTypeKind.Guid } =>
                TryGetSemanticString(value, out var guid) && System.Guid.TryParse(guid, out _),
            ScalarTypeRef { Kind: ScalarTypeKind.Date } => value.TryGetDateOnly(out _),
            ScalarTypeRef { Kind: ScalarTypeKind.DateTime or ScalarTypeKind.Instant } =>
                value.TryGetDateTimeOffset(out _),
            ScalarTypeRef { Kind: ScalarTypeKind.Bytes } => value.Kind == ObservationValueKind.Bytes,
            EnumTypeRef enumeration =>
                TryGetSemanticString(value, out var member)
                && enumeration.Members.Contains(member, StringComparer.Ordinal),
            EntityReferenceTypeRef =>
                TryGetSemanticString(value, out var reference)
                && !string.IsNullOrWhiteSpace(reference),
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException("The canonical value does not match its semantic field type.");
        return value;
    }

    static string RequireGuidValue(ObservationValue value)
    {
        var text = value.GetRequiredString();
        _ = System.Guid.Parse(text);
        return text;
    }

    static string RequireEnumValue(RelationDtoCompiledBinding binding, ObservationValue value)
    {
        var text = value.GetRequiredString();
        if (binding.Definition.Type is not EnumTypeRef enumeration
            || !enumeration.Members.Contains(text, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The canonical value is not a declared semantic enum member.");
        }
        return text;
    }

    static string RequireEntityReference(ObservationValue value)
    {
        var text = value.GetRequiredString();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("The canonical value is not a non-empty entity reference.");
        return text;
    }

    static bool TryGetSemanticString(ObservationValue value, out string text)
    {
        if (value.Kind is ObservationValueKind.String
            or ObservationValueKind.DateTimeOffset
            or ObservationValueKind.DateOnly
            or ObservationValueKind.TimeOnly
            or ObservationValueKind.TimeSpan)
        {
            text = value.GetString() ?? string.Empty;
            return true;
        }
        text = string.Empty;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static T Value<T, TValue>(TValue value)
        where TValue : struct
    {
        if (typeof(T) == typeof(TValue))
            return Unsafe.As<TValue, T>(ref value);

        if (typeof(T) == typeof(TValue?))
        {
            TValue? nullable = value;
            return Unsafe.As<TValue?, T>(ref nullable);
        }

        throw new InvalidOperationException("Compiled DTO value type does not match its conversion kernel.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static T Reference<T>(object value) => (T)value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static T Cast<T>(object? value) => value is null ? default! : (T)value;

    internal interface IConverter<T>
    {
        static abstract T Convert(in ObservationValue value, RelationDtoCompiledBinding binding);
    }

    internal readonly struct ObservationConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, ObservationValue>(RequireSemanticValue(binding, value));
    }

    internal readonly struct BooleanConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, bool>(value.GetBoolean());
    }

    internal readonly struct Int32Converter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, int>(value.GetInt32());
    }

    internal readonly struct Int64Converter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, long>(value.GetInt64());
    }

    internal readonly struct DecimalConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, decimal>(value.GetDecimal());
    }

    internal readonly struct StringConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Reference<T>(value.GetRequiredString());
    }

    internal readonly struct GuidConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, Guid>(System.Guid.Parse(value.GetRequiredString()));
    }

    internal readonly struct DateConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, DateOnly>(value.GetDateOnly());
    }

    internal readonly struct DateTimeConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, DateTime>(value.GetDateTimeOffset().DateTime);
    }

    internal readonly struct InstantConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, DateTimeOffset>(value.GetDateTimeOffset());
    }

    internal readonly struct ReadOnlyBytesConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, ReadOnlyMemory<byte>>(value.GetBytes());
    }

    internal readonly struct ByteArrayConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Reference<T>(value.GetBytes().ToArray());
    }

    internal readonly struct EnumConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Cast<T>(Enum.Parse(
                Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T),
                RequireEnumValue(binding, value),
                ignoreCase: false));
    }

    internal readonly struct Int32ToInt64Converter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, long>(value.GetInt32());
    }

    internal readonly struct Int32ToDecimalConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, decimal>(value.GetInt32());
    }

    internal readonly struct Int64ToDecimalConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Value<T, decimal>(value.GetInt64());
    }

    internal readonly struct GuidStringConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Reference<T>(RequireGuidValue(value));
    }

    internal readonly struct EnumStringConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Reference<T>(RequireEnumValue(binding, value));
    }

    internal readonly struct EntityReferenceStringConverter<T> : IConverter<T>
    {
        public static T Convert(in ObservationValue value, RelationDtoCompiledBinding binding) =>
            Reference<T>(RequireEntityReference(value));
    }
}

sealed class RelationDtoRowMappingException : Exception
{
    internal RelationDtoRowMappingException(
        RelationDtoCompiledBinding binding,
        string diagnosticMessage,
        Exception? innerException = null)
        : base(diagnosticMessage, innerException)
    {
        Binding = binding;
        DiagnosticMessage = diagnosticMessage;
    }

    internal RelationDtoCompiledBinding Binding { get; }
    internal string DiagnosticMessage { get; }
}
