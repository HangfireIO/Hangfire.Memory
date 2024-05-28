// This file is part of Hangfire.InMemory. Copyright © 2020 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.InMemory.Entities;
using Hangfire.States;
using Hangfire.Storage;

namespace Hangfire.InMemory
{
    internal sealed class InMemoryTransaction<TKey> : JobStorageTransaction, IInMemoryCommand<TKey>
        where TKey : IComparable<TKey>
    {
        private readonly LinkedList<IInMemoryCommand<TKey, object>> _commands = new LinkedList<IInMemoryCommand<TKey, object>>();
        private readonly HashSet<QueueEntry<TKey>> _enqueued = new HashSet<QueueEntry<TKey>>();
        private readonly InMemoryConnection<TKey> _connection;
        private readonly List<IDisposable> _acquiredLocks = new List<IDisposable>();

        public InMemoryTransaction([NotNull] InMemoryConnection<TKey> connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public override void Dispose()
        {
            foreach (var acquiredLock in _acquiredLocks)
            {
                acquiredLock.Dispose();
            }

            base.Dispose();
        }

        public override void Commit()
        {
            _connection.Dispatcher.QueryWriteAndWait(this);
        }

        public override void AcquireDistributedLock(string resource, TimeSpan timeout)
        {
            var disposableLock = _connection.AcquireDistributedLock(resource, timeout);
            _acquiredLocks.Add(disposableLock);
        }

        public override string CreateJob([NotNull] Job job, [NotNull] IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            var entry = new JobEntry<TKey>(
                _connection.KeyProvider.GetUniqueKey(),
                InvocationData.SerializeJob(job),
                parameters,
                _connection.Dispatcher.GetMonotonicTime());

            AddCommand(new InMemoryCommands.JobCreate<TKey>(entry, expireIn));
            return _connection.KeyProvider.ToString(entry.Key);
        }

        public override void SetJobParameter(
            [NotNull] string id,
            [NotNull] string name,
            [CanBeNull] string value)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (name == null) throw new ArgumentNullException(nameof(name));

            if (!_connection.KeyProvider.TryParse(id, out var key))
            {
                return;
            }

            AddCommand(new InMemoryCommands.JobSetParameter<TKey>(key, name, value));
        }

        public override void ExpireJob([NotNull] string jobId, TimeSpan expireIn)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            if (!_connection.KeyProvider.TryParse(jobId, out var key))
            {
                return;
            }

            var now = _connection.Dispatcher.GetMonotonicTime();
            AddCommand(new InMemoryCommands.JobExpire<TKey>(key, expireIn, now));
        }

        public override void PersistJob([NotNull] string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            if (!_connection.KeyProvider.TryParse(jobId, out var key))
            {
                return;
            }

            AddCommand(new InMemoryCommands.JobExpire<TKey>(key, expireIn: null, now: null));
        }

        public override void SetJobState([NotNull] string jobId, [NotNull] IState state)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Name == null) throw new ArgumentException("Name property must not return null.", nameof(state));

            if (!_connection.KeyProvider.TryParse(jobId, out var key))
            {
                return;
            }

            // IState can be implemented by user, and potentially can throw exceptions.
            // Getting data here, out of the dispatcher thread, to avoid killing it.
            var entry = new StateEntry(
                state.Name,
                state.Reason,
                state.SerializeData(),
                _connection.Dispatcher.GetMonotonicTime());

            AddCommand(new InMemoryCommands.JobAddState<TKey>(key, entry, setAsCurrent: true));
        }

        public override void AddJobState([NotNull] string jobId, [NotNull] IState state)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (state == null) throw new ArgumentNullException(nameof(state));

            if (!_connection.KeyProvider.TryParse(jobId, out var key))
            {
                return;
            }

            // IState can be implemented by user, and potentially can throw exceptions.
            // Getting data here, out of the dispatcher thread, to avoid killing it.
            var entry = new StateEntry(
                state.Name,
                state.Reason,
                state.SerializeData(),
                _connection.Dispatcher.GetMonotonicTime());

            AddCommand(new InMemoryCommands.JobAddState<TKey>(key, entry, setAsCurrent: false));
        }

        public override void AddToQueue([NotNull] string queue, [NotNull] string jobId)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            if (!_connection.KeyProvider.TryParse(jobId, out var key))
            {
                return;
            }

            AddCommand(new InMemoryCommands.QueueEnqueue<TKey>(queue, key, _enqueued));
        }

        public override void RemoveFromQueue([NotNull] IFetchedJob fetchedJob)
        {
            // Nothing to do here
        }

        public override void IncrementCounter([NotNull] string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            AddCommand(new InMemoryCommands.CounterIncrement<TKey>(key, value: 1, expireIn: null, now: null));
        }

        public override void IncrementCounter([NotNull] string key, TimeSpan expireIn)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            var now = _connection.Dispatcher.GetMonotonicTime();
            AddCommand(new InMemoryCommands.CounterIncrement<TKey>(key, value: 1, expireIn, now));
        }

        public override void DecrementCounter([NotNull] string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            AddCommand(new InMemoryCommands.CounterIncrement<TKey>(key, value: -1, expireIn: null, now: null));
        }

        public override void DecrementCounter([NotNull] string key, TimeSpan expireIn)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            var now = _connection.Dispatcher.GetMonotonicTime();
            AddCommand(new InMemoryCommands.CounterIncrement<TKey>(key, value: -1, expireIn, now));
        }

        public override void AddToSet([NotNull] string key, [NotNull] string value)
        {
            AddToSet(key, value, score: 0.0D);
        }

        public override void AddToSet([NotNull] string key, [NotNull] string value, double score)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (value == null) throw new ArgumentNullException(nameof(value));

            AddCommand(new InMemoryCommands.SortedSetAdd<TKey>(key, value, score));
        }

        public override void RemoveFromSet([NotNull] string key, [NotNull] string value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (value == null) throw new ArgumentNullException(nameof(value));

            AddCommand(new InMemoryCommands.SortedSetRemove<TKey>(key, value));
        }

        public override void InsertToList([NotNull] string key, [NotNull] string value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (value == null) throw new ArgumentNullException(nameof(value));

            AddCommand(new InMemoryCommands.ListInsert<TKey>(key, value));
        }

        public override void RemoveFromList([NotNull] string key, [NotNull] string value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (value == null) throw new ArgumentNullException(nameof(value));

            AddCommand(new InMemoryCommands.ListRemoveAll<TKey>(key, value));
        }

        public override void TrimList([NotNull] string key, int keepStartingFrom, int keepEndingAt)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            AddCommand(new InMemoryCommands.ListTrim<TKey>(key, keepStartingFrom, keepEndingAt));
        }

        public override void SetRangeInHash([NotNull] string key, [NotNull] IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (keyValuePairs == null) throw new ArgumentNullException(nameof(keyValuePairs));

            AddCommand(new InMemoryCommands.HashSetRange<TKey>(key, keyValuePairs));
        }

        public override void RemoveHash([NotNull] string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            AddCommand(new InMemoryCommands.HashRemove<TKey>(key));
        }

        public override void AddRangeToSet([NotNull] string key, [NotNull] IList<string> items)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (items == null) throw new ArgumentNullException(nameof(items));

            foreach (var item in items)
            {
                if (item == null) throw new ArgumentException("The list of items must not contain any `null` entries.", nameof(items));
            }

            if (items.Count == 0) return;

            AddCommand(new InMemoryCommands.SortedSetAddRange<TKey>(key, items));
        }

        public override void RemoveSet([NotNull] string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            AddCommand(new InMemoryCommands.SortedSetDelete<TKey>(key));
        }

        public override void ExpireHash([NotNull] string key, TimeSpan expireIn)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            var now = _connection.Dispatcher.GetMonotonicTime();
            AddCommand(new InMemoryCommands.HashExpire<TKey>(key, expireIn, now));
        }

        public override void ExpireList([NotNull] string key, TimeSpan expireIn)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            var now = _connection.Dispatcher.GetMonotonicTime();
            AddCommand(new InMemoryCommands.ListExpire<TKey>(key, expireIn, now));
        }

        public override void ExpireSet([NotNull] string key, TimeSpan expireIn)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            var now = _connection.Dispatcher.GetMonotonicTime();
            AddCommand(new InMemoryCommands.SortedSetExpire<TKey>(key, expireIn, now));
        }

        public override void PersistHash([NotNull] string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            AddCommand(new InMemoryCommands.HashExpire<TKey>(key, expireIn: null, now: null));
        }

        public override void PersistList([NotNull] string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            AddCommand(new InMemoryCommands.ListExpire<TKey>(key, expireIn: null, now: null));
        }

        public override void PersistSet([NotNull] string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            AddCommand(new InMemoryCommands.SortedSetExpire<TKey>(key, expireIn: null, now: null));
        }

        private void AddCommand(IInMemoryCommand<TKey, object> action)
        {
            _commands.AddLast(action);
        }

        object IInMemoryCommand<TKey, object>.Execute(InMemoryState<TKey> state)
        {
            try
            {
                foreach (var command in _commands)
                {
                    command.Execute(state);
                }
            }
            finally
            {
                foreach (var acquiredLock in _acquiredLocks)
                {
                    acquiredLock.Dispose();
                }
            }

            foreach (var queue in _enqueued)
            {
                queue.SignalOneWaitNode();
            }

            return null;
        }
    }
}