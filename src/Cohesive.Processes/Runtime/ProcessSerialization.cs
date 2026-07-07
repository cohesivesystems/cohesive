using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Processes.Runtime;

static class ProcessSerialization
{
    public static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new StructuredQuantityJsonConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}