namespace Coop.Core.Server;

/// <summary>
/// Bounds a complete client-to-server transport payload before it is copied or deserialized.
/// </summary>
internal static class ServerInboundPayloadAdmission
{
    // The only large client-to-server message is NetworkTransferNewHero. Captured successful
    // sessions produced equivalent NetworkNewPlayerHeroCreated payloads of about 4.12 MB; 8 MiB
    // leaves roughly 2x headroom while bounding GetRemainingBytes and protobuf byte-array copies.
    internal const int MaximumPayloadBytes = 8 * 1024 * 1024;

    internal static bool IsAllowed(int payloadBytes) =>
        payloadBytes > 0 && payloadBytes <= MaximumPayloadBytes;
}
