using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

// One wire policy for evidence documents containing portable observation values.
internal static class AssuranceJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();
    static JsonSerializerOptions CreateOptions()
    {
        var options = StrictDocumentJson.CreateOptions();
        options.Converters.Insert(0, PortableValueJsonConverter.TaggedObservationValues);
        return options;
    }
}
