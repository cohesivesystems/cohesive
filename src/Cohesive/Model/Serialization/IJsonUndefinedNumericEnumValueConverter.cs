namespace Cohesive.Model.Serialization;

/// <summary>
/// Marks a JSON enum converter that writes declared values as strings while retaining undefined underlying values
/// as JSON numbers.
/// </summary>
/// <remarks>
/// The CLR shape metadata provider uses this marker to retain the open numeric portion of the property wire contract
/// for target-language code generation.
/// </remarks>
public interface IJsonUndefinedNumericEnumValueConverter
{
}
