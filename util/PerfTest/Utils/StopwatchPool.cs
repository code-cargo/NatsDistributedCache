using System.Buffers;
using System.Diagnostics;

namespace CodeCargo.Nats.DistributedCache.PerfTest.Utils
{
    /// <summary>
    /// Provides a pooled Stopwatch to reduce allocations in performance-critical code
    /// </summary>
    public static class StopwatchPool
    {
        private static readonly ArrayPool<Stopwatch> _pool = ArrayPool<Stopwatch>.Shared;

        /// <summary>
        /// Rents a stopwatch from the pool and starts it
        /// </summary>
        /// <returns>A PooledStopwatch that wraps the timer and handles returning it to the pool</returns>
        public static PooledStopwatch Rent()
        {
            // Get a single stopwatch from the pool
            var stopwatches = _pool.Rent(1);
            var stopwatch = stopwatches[0] ?? new Stopwatch();

            // Reset and start
            stopwatch.Reset();
            stopwatch.Start();

            return new PooledStopwatch(stopwatch, stopwatches);
        }

        /// <summary>
        /// A disposable wrapper for a pooled Stopwatch
        /// </summary>
        public readonly struct PooledStopwatch : IDisposable
        {
            private readonly Stopwatch _stopwatch;
            private readonly Stopwatch[] _array;

            internal PooledStopwatch(Stopwatch stopwatch, Stopwatch[] array)
            {
                _stopwatch = stopwatch;
                _array = array;
            }

            /// <summary>
            /// Gets the elapsed time of the stopwatch
            /// </summary>
            public TimeSpan Elapsed => _stopwatch.Elapsed;

            /// <summary>
            /// Stops the timer
            /// </summary>
            public void Stop() => _stopwatch.Stop();

            /// <summary>
            /// Returns the stopwatch to the pool
            /// </summary>
            public void Dispose()
            {
                // Ensure stopwatch is stopped
                _stopwatch.Stop();

                // Store back in array at index 0
                _array[0] = _stopwatch;

                // Return array to pool
                _pool.Return(_array);
            }
        }
    }
}
