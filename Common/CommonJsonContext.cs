using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;

namespace Common
{
    [JsonSourceGenerationOptions(
            GenerationMode = JsonSourceGenerationMode.Default,
            PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
            WriteIndented = false)]
    [JsonSerializable(typeof(Packet))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(byte[]))]
    public partial class CommonJsonContext : JsonSerializerContext
    {
        // Fix for CS0534: Implementing the GeneratedSerializerOptions property
        //protected override JsonSerializerOptions? GeneratedSerializerOptions { get; } = new JsonSerializerOptions();

        // Fix for CS0534: Implementing the GetTypeInfo method
        //public override JsonTypeInfo? GetTypeInfo(Type type)
        //{
        //    return type == typeof(Packet) ? Packet :
        //           type == typeof(Dictionary<string, string>) ? DictionaryStringString :
        //           type == typeof(string[]) ? StringArray :
        //           type == typeof(byte[]) ? ByteArray :
        //           null;
        //}

        // Fix for CS7036: Adding a constructor to pass the required 'options' parameter
        //public CommonJsonContext(JsonSerializerOptions? options = null) : base(options)
        //{
        //}
    }
}
