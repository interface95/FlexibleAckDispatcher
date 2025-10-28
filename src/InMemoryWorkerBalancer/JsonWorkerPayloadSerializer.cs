using System;
using System.Text.Json;
using InMemoryWorkerBalancer.Abstractions;

namespace InMemoryWorkerBalancer;

/// <summary>
/// Default JSON based payload serializer used by the worker runtime.
/// </summary>
public sealed class JsonWorkerPayloadSerializer : IWorkerPayloadSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Initializes a new instance of <see cref="JsonWorkerPayloadSerializer"/>.
    /// </summary>
    public JsonWorkerPayloadSerializer(JsonSerializerOptions? options = null)
    {
        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    /// <summary>
    /// Shared singleton instance configured with default options.
    /// </summary>
    public static JsonWorkerPayloadSerializer Default { get; } = new();

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize<T>(T value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, _options);
    }

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlyMemory<byte> payload)
    {
        var value = JsonSerializer.Deserialize<T>(payload.Span, _options);
        if (value is null && typeof(T).IsValueType == false)
        {
            throw new InvalidOperationException($"Failed to deserialize payload to type {typeof(T)}.");
        }

        return value!;
    }
}

