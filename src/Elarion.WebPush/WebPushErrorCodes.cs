namespace Elarion.WebPush;

/// <summary>
/// The stable codes of the <c>AppError</c>s <see cref="WebPushSubscriptionService"/> returns, so a client branches on
/// the code instead of the English message. Declare them with <c>[ProducesError]</c> on a handler that forwards them.
/// </summary>
public static class WebPushErrorCodes {
    /// <summary>
    /// The subscription cannot be kept: its endpoint is missing, too long, not an absolute https URL or not on an
    /// allowed push service, its keys are missing or not valid, or the store refused it; a validation error. One code
    /// for all of them, so nothing tells a caller that an endpoint belongs to someone else.
    /// </summary>
    public const string SubscriptionInvalid = "web_push.subscription_invalid";
}
