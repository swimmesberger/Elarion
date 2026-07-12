namespace Elarion.Streams;

/// <summary>
/// Thrown to a <see cref="StreamOverflowMode.Cancel"/> subscriber whose buffer overflowed: the consumer
/// fell too far behind the publisher and was removed rather than delaying it. Re-subscribe (typically
/// with <see cref="StreamSubscribeOptions.ResumeAfterSequence"/>) to continue.
/// </summary>
public sealed class StreamLaggedException(string message) : Exception(message);
