using System.Text;
using System.Text.Json;

namespace Merconiq.Core.Models;

/// <summary>Defines the serialized UTF-8 size limit for outbound webhook envelopes.</summary>
public static class WebhookPayloadPolicy
{
    public const int MaximumSerializedEnvelopeBytes = 256 * 1024;
    public const string OversizedEnvelopeDiagnostic =
        "Serialized webhook event envelope exceeds the 256 KiB UTF-8 limit.";

    /// <summary>Serializes an envelope without allowing its output buffer to exceed the limit.</summary>
    public static string Serialize<T>(WebhookEvent<T> webhookEvent)
    {
        ArgumentNullException.ThrowIfNull(webhookEvent);

        using var output = new BoundedMemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            JsonSerializer.Serialize(writer, webhookEvent);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    /// <summary>Checks a stored serialized envelope using its UTF-8 byte count.</summary>
    public static bool ExceedsLimit(string serializedEnvelope)
    {
        ArgumentNullException.ThrowIfNull(serializedEnvelope);
        return Encoding.UTF8.GetByteCount(serializedEnvelope) > MaximumSerializedEnvelopeBytes;
    }

    private sealed class BoundedMemoryStream : MemoryStream
    {
        public BoundedMemoryStream() : base(1024) { }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinLimit(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWithinLimit(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureWithinLimit(1);
            base.WriteByte(value);
        }

        private void EnsureWithinLimit(int count)
        {
            if (count < 0 || Position > MaximumSerializedEnvelopeBytes - count)
            {
                throw new WebhookPayloadTooLargeException();
            }
        }
    }
}

/// <summary>Raised when an outbound webhook envelope exceeds the configured serialized size.</summary>
public sealed class WebhookPayloadTooLargeException : InvalidOperationException
{
    public WebhookPayloadTooLargeException()
        : base(WebhookPayloadPolicy.OversizedEnvelopeDiagnostic) { }
}
