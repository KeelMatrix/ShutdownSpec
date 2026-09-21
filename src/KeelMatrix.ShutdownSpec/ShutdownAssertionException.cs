namespace KeelMatrix.ShutdownSpec;

/// <summary>Thrown when a framework-neutral shutdown result assertion fails.</summary>
public sealed class ShutdownAssertionException : Exception
{
    internal ShutdownAssertionException(string message) : base(message)
    {
    }
}
