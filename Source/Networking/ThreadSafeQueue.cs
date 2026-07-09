using System;
using System.Collections.Generic;

namespace Overlord
{
    /// <summary>
    /// Generic thread-safe queue for cross-thread message passing.
    /// DrainTo copies items out under lock, then processes unlocked so
    /// the producer thread is never blocked by slow handlers.
    /// </summary>
    public class ThreadSafeQueue<T>
    {
        private readonly Queue<T> queue = new Queue<T>();
        private readonly object lockObj = new object();

        public void Enqueue(T item)
        {
            lock (lockObj)
            {
                queue.Enqueue(item);
            }
        }

        public bool TryDequeue(out T item)
        {
            lock (lockObj)
            {
                if (queue.Count > 0)
                {
                    item = queue.Dequeue();
                    return true;
                }
            }
            item = default(T);
            return false;
        }

        /// <summary>
        /// Drain all items and process them outside the lock.
        /// </summary>
        public void DrainTo(Action<T> handler)
        {
            List<T> snapshot;
            lock (lockObj)
            {
                if (queue.Count == 0)
                    return;
                snapshot = new List<T>(queue);
                queue.Clear();
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                try
                {
                    handler(snapshot[i]);
                }
                catch (Exception ex)
                {
                    LogUtil.Warn($"Error processing queued item: {ex.Message}");
                }
            }
        }

        public int Count
        {
            get
            {
                lock (lockObj)
                {
                    return queue.Count;
                }
            }
        }

        public void Clear()
        {
            lock (lockObj)
            {
                queue.Clear();
            }
        }
    }
}
