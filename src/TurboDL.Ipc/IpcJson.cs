using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboDL.Core.Ipc;

/// <summary>JSON for the pipe and for Chrome: camelCase, no indentation (one object per line), nulls left out.</summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(IpcRequest request) => JsonSerializer.Serialize(request, Options);

    public static string Serialize(IpcReply reply) => JsonSerializer.Serialize(reply, Options);

    /// <summary>Parses one request. Throws <see cref="JsonException"/> on malformed JSON or a non-object.</summary>
    public static IpcRequest ParseRequest(string json) =>
        JsonSerializer.Deserialize<IpcRequest>(json, Options) ?? throw new JsonException("Expected a JSON object.");

    /// <summary>Parses one reply. Throws <see cref="JsonException"/> on malformed JSON or a non-object.</summary>
    public static IpcReply ParseReply(string json) =>
        JsonSerializer.Deserialize<IpcReply>(json, Options) ?? throw new JsonException("Expected a JSON object.");
}
