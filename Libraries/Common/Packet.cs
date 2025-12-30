using System.Text.Json.Serialization;

namespace Common;

public class Packet {
    public string? ClientID { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

public enum PacketStatus {
    Ok,
    Disconnected,
    Error
}

[JsonSourceGenerationOptions(
            GenerationMode = JsonSourceGenerationMode.Default,
            PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
            WriteIndented = false)]
[JsonSerializable(typeof(Packet))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(ClientInfo))]
[JsonSerializable(typeof(ClientInfo[]))]
public partial class CommonJsonContext : JsonSerializerContext {
}
