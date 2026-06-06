using System;
using System.Collections.Generic;

namespace opennest_2
{
    /// <summary>
    /// Serializes solves that share a non-reentrant native engine (the engines keep cancel/progress/cache in
    /// process-global state, so only one solve per engine may run at a time). One acquirer holds the gate;
    /// others QUEUE and are WOKEN in order when the holder releases — so several nesting components in one
    /// document run one after another with no manual retry.
    ///
    /// There is one gate PER ENGINE:
    ///   <see cref="Nfp"/>     -> nfp_nest.dll      (OpenNest1 + OpenNest2 share it)
    ///   <see cref="Physics"/> -> nest_physics.dll  (OpenNestCollision)
    /// Components on DIFFERENT engines never block each other.
    /// </summary>
    public sealed class EngineGate
    {
        public static readonly EngineGate Nfp = new EngineGate();
        public static readonly EngineGate Physics = new EngineGate();

        private readonly object _gate = new object();
        private bool _busy = false;
        private readonly List<Action> _waiters = new List<Action>();

        /// <summary>
        /// Returns true and takes the gate, or returns false and enqueues <paramref name="wake"/> (which is
        /// invoked, in FIFO order, when the gate frees). <paramref name="wake"/> MUST be a stable delegate per
        /// component (cache it in a field) so enqueue/dequeue match by reference.
        /// </summary>
        public bool TryAcquire(Action wake)
        {
            lock (_gate)
            {
                if (_busy) { if (wake != null && !_waiters.Contains(wake)) _waiters.Add(wake); return false; }
                _busy = true;
                if (wake != null) _waiters.Remove(wake);
                return true;
            }
        }

        /// <summary>Free the gate and wake the next queued component (if any).</summary>
        public void Release()
        {
            Action next = null;
            lock (_gate) { _busy = false; if (_waiters.Count > 0) next = _waiters[0]; }  // peek; it removes itself on acquire
            next?.Invoke();
        }

        /// <summary>Drop a component out of the queue (e.g. when it is Stopped or removed).</summary>
        public void Dequeue(Action wake) { lock (_gate) { _waiters.Remove(wake); } }
    }
}
