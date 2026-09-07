namespace ACE.Server.Network.Enum
{
    /// <summary>
    /// Result of Session.TryClaimLoginRequest(). Used to distinguish a genuinely duplicate/retransmitted
    /// LoginRequest packet (safe to silently drop) from two different login attempts racing in on the
    /// same session concurrently (which should be surfaced and terminated cleanly, not silently ignored).
    /// </summary>
    public enum LoginRequestClaimResult
    {
        LoginClaimed,
        DuplicatePacket,
        SessionClash,
    }
}
