using System.Text.Json;
using System.Text.Json.Serialization;
using AspireFirst.Orders;

// The isolated build generates a writer for the revised Orders model. The exercise host retains the old
// generated reader under the explicitly selected required-constructor policy; no live transport is needed.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(OrderDetails))]
internal partial class OrdersWireContext : JsonSerializerContext;
