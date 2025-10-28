using System;
using System.Text.Json;
using InMemoryWorkerBalancer.Abstractions;

namespace InMemoryWorkerBalancer;

/// <summary>
/// Default JSON based payload serializer used by the worker runtime.
/// </summary>
public sealed class JsonWorkerPayloadSerializer : IWorkerPayloadSerializer
{
    private const int DefaultMaxPayloadSize = 10 * 1024 * 1024; // 10 MiB

    private readonly JsonSerializerOptions _options;
    private readonly int _maxPayloadSize;

    /// <summary>
    /// Initializes a new instance of <see cref="JsonWorkerPayloadSerializer"/>.
    /// </summary>
    public JsonWorkerPayloadSerializer(JsonSerializerOptions? options = null, int maxPayloadSize = DefaultMaxPayloadSize)
    {
        if (maxPayloadSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadSize), "Max payload size must be greater than zero.");

        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _maxPayloadSize = maxPayloadSize;
    }

    /// <summary>
    /// Shared singleton instance configured with default options.
    /// </summary>
    public static JsonWorkerPayloadSerializer Default { get; } = new();

    /// <summary>
    /// Gets the maximum payload size (in bytes) allowed during deserialization.
    /// </summary>
    public int MaxPayloadSize => _maxPayloadSize;

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Serialize<T>(T value)
    {
        var buffer = JsonSerializer.SerializeToUtf8Bytes(value, _options);
        if (buffer.Length > _maxPayloadSize)
            throw new InvalidOperationException($"Payload too large to serialize: {buffer.Length} bytes (limit: {_maxPayloadSize}).");

        return buffer;
    }

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > _maxPayloadSize)
            throw new InvalidOperationException($"Payload too large: {payload.Length} bytes (limit: {_maxPayloadSize}).");

        var value = JsonSerializer.Deserialize<T>(payload.Span, _options);
        if (value is null && typeof(T).IsValueType == false)
            throw new InvalidOperationException($"Failed to deserialize payload to type {typeof(T)}.");

        return value!;
    }
}

