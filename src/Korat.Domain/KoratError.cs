namespace Korat.Domain;

public static class KoratError
{
    public static string Message(KoratErrorCode code) => code switch
    {
        KoratErrorCode.PendingApproval => "Access is pending approval.",
        KoratErrorCode.AccessDenied => "Access was denied.",
        KoratErrorCode.GrantRevoked => "Grant was revoked.",
        KoratErrorCode.ServerDisabled => "MCP server is disabled.",
        KoratErrorCode.ServerUnavailable => "MCP server is unavailable.",
        KoratErrorCode.OfflineNode => "Node is offline.",
        KoratErrorCode.PayloadLimitExceeded => "Payload limit exceeded.",
        KoratErrorCode.CryptoFailure => "Relay crypto failure.",
        KoratErrorCode.DuplicateServerName => "An MCP server with this name already exists.",
        KoratErrorCode.NotAuthorized => "Not authorized.",
        KoratErrorCode.NotFound => "Resource not found.",
        KoratErrorCode.InvalidStateTransition => "The request is not in a state that allows this operation.",
        KoratErrorCode.Validation => "Request validation failed.",
        KoratErrorCode.GrainTimeout => "Call exceeded the 10-second timeout.",
        KoratErrorCode.NodeWaking => "Node is waking up — best-effort wake sent; retry in ~30 s.",
        KoratErrorCode.DataStoreUnavailable => "A transient data store error occurred; please retry.",
        KoratErrorCode.DuplicateChannelBotId => "This channel bot is already bound to another channel.",
        KoratErrorCode.ChannelAlreadyVerified => "This channel is already verified.",
        KoratErrorCode.ServerNeedsReauth => "MCP server needs re-authorization.",
        _ => "An error occurred."
    };

    public static string Code(KoratErrorCode code) => code.ToString();
}
