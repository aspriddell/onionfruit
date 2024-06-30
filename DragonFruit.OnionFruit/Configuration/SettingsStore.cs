﻿// OnionFruit Copyright DragonFruit Network <inbox@dragonfruit.network>
// Licensed under LGPL-3.0. Refer to the LICENCE file for more info

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;

namespace DragonFruit.OnionFruit.Configuration
{
    public abstract class SettingsStore<TKey> : IDisposable where TKey : Enum
    {
        protected readonly IDictionary<TKey, object> ConfigStore = new ConcurrentDictionary<TKey, object>();
        protected readonly CompositeDisposable Subscriptions = new();
        protected readonly BehaviorSubject<bool> IsLoaded = new(false);

        private readonly object _saveLock = new();
        private long _lastSaveValue;

        protected virtual void RegisterSettings()
        {
        }

        protected abstract void LoadConfiguration();
        protected abstract void SaveConfiguration();

        /// <summary>
        /// Gets the current value of a specified configuration key
        /// </summary>
        public TValue GetValue<TValue>(TKey key) => GetRootSubject<TValue>(key).Value;

        /// <summary>
        /// Gets a source for observing changes to a configuration value
        /// </summary>
        /// <param name="key">The key to get an observable notification stream for</param>
        public IObservable<TValue> GetObservableValue<TValue>(TKey key) => GetRootSubject<TValue>(key);

        /// <summary>
        /// Sets a configuration value, informing observers of the change
        /// </summary>
        public void SetValue<TValue>(TKey key, TValue value)
        {
            var subject = GetRootSubject<TValue>(key);

            if (subject == null)
            {
                throw new ArgumentException($"Key {key} does not exist in the configuration store");
            }

            if (subject.Value?.Equals(value) == true)
            {
                return;
            }

            subject.OnNext(value);
        }

        private BehaviorSubject<TValue> GetRootSubject<TValue>(TKey key)
        {
            if (!ConfigStore.TryGetValue(key, out var subject))
            {
                return null;
            }

            if (subject is BehaviorSubject<TValue> typedSubject)
            {
                return typedSubject;
            }

            throw new InvalidCastException($"Subject is not of the expected type. Expected <{subject.GetType().GetGenericParameterConstraints().Single().Name}>, got <{typeof(TValue).Name}>");
        }

        protected IObservable<TValue> RegisterOption<TValue>(TKey key, TValue defaultValue, out BehaviorSubject<TValue> subject)
        {
            if (ConfigStore.TryGetValue(key, out var cachedSubject) && cachedSubject is BehaviorSubject<TValue>)
            {
                throw new ArgumentException($"Key {key} already exists in the configuration store");
            }

            subject = new BehaviorSubject<TValue>(defaultValue);
            var watcher = subject.CombineLatest(IsLoaded).Where(x => x.Second).Select(x => x.First).DistinctUntilChanged();

            ConfigStore.Add(key, subject);
            watcher.ObserveOn(RxApp.TaskpoolScheduler).Select(_ => QueueSave()).Subscribe().DisposeWith(Subscriptions);

            return watcher;
        }

        private async Task<Unit> QueueSave()
        {
            var lastSave = Interlocked.Increment(ref _lastSaveValue);

            await Task.Delay(250).ConfigureAwait(false);

            if (lastSave == Interlocked.Read(ref _lastSaveValue))
            {
                lock (_saveLock)
                {
                    SaveConfiguration();
                }
            }

            return Unit.Default;
        }

        public void Dispose()
        {
            Subscriptions?.Dispose();
        }
    }
}