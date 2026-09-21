using System.Threading;

namespace KeelMatrix.ShutdownSpec;

/// <summary>Represents an application-owned lifecycle checkpoint.</summary>
public sealed class ShutdownProbe
{
    private int _observed;

    /// <summary>Creates a named lifecycle checkpoint.</summary>
    /// <param name="name">A stable, non-empty diagnostic name of at most 100 characters.</param>
    public ShutdownProbe(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A probe name is required.", nameof(name));
        if (name.Length > 100) throw new ArgumentException("A probe name must be 100 characters or fewer.", nameof(name));
        Name = name;
    }

    /// <summary>Gets the diagnostic name of this checkpoint.</summary>
    public string Name { get; }

    /// <summary>Gets whether application code has marked this checkpoint as observed.</summary>
    public bool IsObserved => Volatile.Read(ref _observed) != 0;

    /// <summary>Marks this checkpoint as observed. Repeated calls are harmless.</summary>
    public void MarkObserved() => Interlocked.Exchange(ref _observed, 1);

    /// <summary>Registers this checkpoint to be marked when a cancellation token is canceled.</summary>
    /// <param name="cancellationToken">The application-owned cancellation token to observe.</param>
    /// <returns>A registration that can be disposed by the caller.</returns>
    public IDisposable ObserveCancellation(CancellationToken cancellationToken)
    {
        return cancellationToken.Register(MarkObserved);
    }
}
