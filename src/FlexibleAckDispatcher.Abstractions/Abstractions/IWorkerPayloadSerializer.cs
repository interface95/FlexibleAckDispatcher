using System;

namespace FlexibleAckDispatcher.Abstractions;

/// <summary>
/// Encodes and decodes payloads transported by the worker runtime.
/// </summary>
public interface IWorkerPayloadSerializer
{
    /// <summary>
    /// Serializes the specified value to a binary representation suitable for transport.
    /// </summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <returns>Binary payload.</returns>
    ReadOnlyMemory<byte> Serialize<T>(T value);

    /// <summary>
    /// Deserializes the binary payload back to the requested type.
    /// </summary>
    /// <typeparam name="T">Target type.</typeparam>
    /// <param name="payload">Binary payload.</param>
    /// <returns>Deserialized value.</returns>
    T Deserialize<T>(ReadOnlyMemory<byte> payload);
}

