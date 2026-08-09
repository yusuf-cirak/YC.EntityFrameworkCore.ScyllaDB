using Microsoft.EntityFrameworkCore.Infrastructure;

namespace YC.EntityFrameworkCore.ScyllaDB.Update.Internal;

/// <summary>
///     Holds the write options currently in scope for a <c>DbContext</c>.
/// </summary>
public interface IScyllaWriteOptionsAccessor
{
    ScyllaWriteOptions? Current { get; }

    IDisposable Push(ScyllaWriteOptions options);
}

/// <inheritdoc cref="IScyllaWriteOptionsAccessor" />
/// <remarks>
///     Scoped to the context and deliberately not thread-safe: a <c>DbContext</c> is not thread-safe either, and
///     a lock here would only hide misuse rather than fix it.
/// </remarks>
public class ScyllaWriteOptionsAccessor : IScyllaWriteOptionsAccessor, IResettableService
{
    private readonly Stack<ScyllaWriteOptions> _scopes = new();

    public virtual ScyllaWriteOptions? Current
        => _scopes.Count == 0 ? null : _scopes.Peek();

    public virtual IDisposable Push(ScyllaWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _scopes.Push(options);

        return new Scope(this);
    }

    public virtual void ResetState()
        => _scopes.Clear();

    public virtual Task ResetStateAsync(CancellationToken cancellationToken = default)
    {
        ResetState();
        return Task.CompletedTask;
    }

    private sealed class Scope(ScyllaWriteOptionsAccessor owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (owner._scopes.Count > 0)
            {
                owner._scopes.Pop();
            }
        }
    }
}
