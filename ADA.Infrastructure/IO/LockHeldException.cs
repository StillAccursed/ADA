namespace ADA.Infrastructure.IO;

/// <summary>An install's lock is held, and the message says by whom and since when.</summary>
internal sealed class LockHeldException(string message) : IOException(message);
