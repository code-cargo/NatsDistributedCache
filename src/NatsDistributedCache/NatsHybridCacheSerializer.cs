// even though this is an IHybridCacheSerializer, it's included in Microsoft.Extensions.Caching.Abstractions
// so we can include it in the NatsDistributedCache package instead of NatsHybridCache to give library
// consumers the option to reduce dependencies

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Hybrid;
using NATS.Client.Core;

namespace CodeCargo.Nats.DistributedCache;

public readonly struct NatsHybridCacheSerializer<T>(INatsSerialize<T> serializer, INatsDeserialize<T> deserializer)
    : IHybridCacheSerializer<T>
{
    public T Deserialize(ReadOnlySequence<byte> source) => deserializer.Deserialize(source)!;

    public void Serialize(T value, IBufferWriter<byte> target) => serializer.Serialize(target, value);
}

public readonly struct NatsHybridCacheSerializerFactory(INatsSerializerRegistry serializerRegistry)
    : IHybridCacheSerializerFactory
{
    public bool TryCreateSerializer<T>([NotNullWhen(true)] out IHybridCacheSerializer<T>? serializer)
    {
        try
        {
            var natsSerializer = serializerRegistry.GetSerializer<T>();
            var natsDeserializer = serializerRegistry.GetDeserializer<T>();
            serializer = new NatsHybridCacheSerializer<T>(natsSerializer, natsDeserializer);
            return true;
        }
        catch (Exception)
        {
            serializer = null;
            return false;
        }
    }
}
