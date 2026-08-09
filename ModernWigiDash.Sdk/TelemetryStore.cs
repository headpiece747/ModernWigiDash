using System;
using System.Threading;

namespace ModernWigiDash.Sdk;

/// <summary>
/// One telemetry-staleness policy shared by every producer store. Owns the
/// cached snapshot, the lock gate, and the staleness decision — consumers ask
/// <see cref="TryReadFresh"/> and cannot skip the check.
///
/// Freshness is measured against the <em>producer timestamp</em> passed to
/// <see cref="Update"/> (never the receive time), so cross-machine clock skew
/// does not affect the decision. A stale snapshot means the producer stopped
/// polling (service disconnected or app suspending), so widgets should render
/// their unavailable state instead of frozen data. A default/empty producer
/// timestamp is treated as "never fresh", so a store that was never updated
/// (or was reset) reads as stale.
///
/// Domain stores (e.g. <c>LhmSensorStore</c>, <c>FrameTimeStore</c>) wrap one
/// instance per record shape and own the DTO-to-record mapping; the per-domain
/// empty value and staleness window are constructor options.
/// </summary>
public sealed class TelemetryStore<TRecord> where TRecord : class
{
    private readonly Lock _gate = new();
    private readonly TRecord _emptyValue;
    private readonly TimeSpan _defaultMaxAge;
    private readonly TimeProvider _timeProvider;
    private TRecord _current;
    private DateTime _lastProducerTimestamp;

    /// <param name="emptyValue">The record a freshly reset store exposes
    /// (e.g. the disconnected/unavailable sentinel of the domain).</param>
    /// <param name="defaultMaxAge">Staleness window used when
    /// <see cref="TryReadFresh"/> is called without an explicit max age.
    /// Defaults to 10 seconds.</param>
    /// <param name="timeProvider">Clock used by <see cref="TryReadFresh"/>
    /// when no per-call clock is supplied. Defaults to
    /// <see cref="TimeProvider.System"/>.</param>
    public TelemetryStore(TRecord emptyValue, TimeSpan? defaultMaxAge = null, TimeProvider? timeProvider = null)
    {
        _emptyValue = emptyValue;
        _defaultMaxAge = defaultMaxAge ?? TimeSpan.FromSeconds(10);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _current = emptyValue;
    }

    /// <summary>Returns the cached snapshot under the gate.</summary>
    public TRecord? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Stores a snapshot. The producer timestamp is preserved — the caller is
    /// responsible for providing it (falling back to the receive time when the
    /// producer did not stamp one).
    /// </summary>
    public void Update(TRecord record, DateTime producerTimestamp)
    {
        lock (_gate)
        {
            _current = record;
            _lastProducerTimestamp = producerTimestamp;
        }
    }

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// freshness decision uses the producer timestamp with an injectable clock.
    /// </summary>
    /// <param name="maxAge">Staleness window; defaults to the constructor's
    /// default max age when null.</param>
    /// <param name="timeProvider">Clock for the freshness decision; defaults to
    /// the constructor's clock when null.</param>
    public TRecord? TryReadFresh(TimeSpan? maxAge = null, TimeProvider? timeProvider = null)
    {
        lock (_gate)
        {
            if (_lastProducerTimestamp == default)
            {
                return null;
            }

            var now = (timeProvider ?? _timeProvider).GetUtcNow().UtcDateTime;
            return now - _lastProducerTimestamp <= (maxAge ?? _defaultMaxAge) ? _current : null;
        }
    }

    /// <summary>
    /// Resets the cache to the store's empty value. Intended for test isolation.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _current = _emptyValue;
            _lastProducerTimestamp = default;
        }
    }
}
