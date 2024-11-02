﻿// OnionFruit Copyright DragonFruit Network <inbox@dragonfruit.network>
// Licensed under LGPL-3.0. Refer to the LICENCE file for more info

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DragonFruit.OnionFruit.Core.Transports;
using DragonFruit.OnionFruit.Database;
using DragonFruit.OnionFruit.Updater;
using DynamicData;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using CodedOutputStream = Google.Protobuf.CodedOutputStream;

namespace DragonFruit.OnionFruit.Configuration
{
    public class OnionFruitSettingsStore : SettingsStore<OnionFruitSetting>
    {
        private const int ConfigVersion = 1;

        private readonly ILogger<OnionFruitSettingsStore> _logger;

        private OnionFruitConfigFile _configFile;

        private IDictionary<OnionFruitSetting, SettingsStoreEntry> _storeEntries = new Dictionary<OnionFruitSetting, SettingsStoreEntry>();
        private IDictionary<OnionFruitSetting, SettingsCollectionEntry> _storeCollections = new Dictionary<OnionFruitSetting, SettingsCollectionEntry>();

        private string ConfigurationFile => Path.Combine(App.StoragePath, "onionfruit.cfg");

        public OnionFruitSettingsStore(ILogger<OnionFruitSettingsStore> logger)
        {
            _logger = logger;

            RegisterSettings();
            LoadConfiguration();

            IsLoaded.OnNext(true);
        }

        /// <summary>
        /// Stores information about a collection of values held in the configuration file
        /// </summary>
        /// <remarks>
        /// Because repeated field properties cannot be replaced (unlike <see cref="SettingsStoreEntry"/>) and have common interfaces regardless of the underlying type,
        /// a <see cref="Func{TResult}"/> can be used to access the underlying value over reflection-based access.
        /// </remarks>
        private record SettingsCollectionEntry(Action<OnionFruitConfigFile> SetFromConfig, Action<OnionFruitConfigFile> SetDefaultValues);

        /// <summary>
        /// Stores information about the accessibility of a setting in the configuration file
        /// </summary>
        /// <param name="SetDefaultValue">A setter for applying the default value, should be stored if the configuration is new or being reset</param>
        private record SettingsStoreEntry([MaybeNull] Action<OnionFruitSettingsStore> SetDefaultValue, Action<OnionFruitConfigFile> SetFromConfig);

        public Version PreviousClientVersion { get; private set; }

        protected override void RegisterSettings()
        {
            RegisterOption(OnionFruitSetting.TorEntryCountryCode, IOnionDatabase.TorCountryCode, nameof(OnionFruitConfigFile.EntryCountryCode));
            RegisterOption(OnionFruitSetting.TorExitCountryCode, IOnionDatabase.TorCountryCode, nameof(OnionFruitConfigFile.ExitCountryCode));

            RegisterOption(OnionFruitSetting.EnableWebsiteLaunchConnect, true, nameof(OnionFruitConfigFile.EnableWebsiteLaunchOnConnect));
            RegisterOption(OnionFruitSetting.EnableWebsiteLaunchDisconnect, false, nameof(OnionFruitConfigFile.EnableWebsiteLaunchOnDisconnect));

            RegisterOption<string>(OnionFruitSetting.WebsiteLaunchConnect, null, nameof(OnionFruitConfigFile.LaunchWebsiteOnConnect));
            RegisterOption<string>(OnionFruitSetting.WebsiteLaunchDisconnect, null, nameof(OnionFruitConfigFile.LaunchWebsiteOnDisconnect));

            RegisterOption(OnionFruitSetting.EnableFirewallPortRestrictions, false, nameof(OnionFruitConfigFile.LimitOutboundConnectionPorts));
            RegisterCollection<uint>(OnionFruitSetting.AllowedFirewallPorts, [80, 443], c => c.AllowedFirewallPorts);

            RegisterOption(OnionFruitSetting.SelectedTransportType, TransportType.None, x => (TransportType)x.SelectedTransportType, (t, c) =>
            {
                c.SelectedTransportType = (TRANSPORT_TYPES)t;
            });
            RegisterCollection(OnionFruitSetting.UserDefinedBridges, [], c => c.UserDefinedBridges);

            RegisterOption(OnionFruitSetting.DisconnectOnTorFailure, false, nameof(OnionFruitConfigFile.DisconnectOnProxyFailure));
            RegisterOption(OnionFruitSetting.EnableErrorReporting, true, nameof(OnionFruitConfigFile.EnableErrorReporting));

            RegisterOption(OnionFruitSetting.EnableDiscordRpc, false, nameof(OnionFruitConfigFile.EnableDiscordRpc));
            RegisterOption(OnionFruitSetting.ExplicitUpdateStream, null, x => x.HasClientUpdateStream ? (UpdateStream?)x.ClientUpdateStream : null, (t, c) =>
            {
                if (t == null)
                {
                    c.ClearClientUpdateStream();
                }
                else
                {
                    c.ClientUpdateStream = (UPDATE_STREAM)t.Value;
                }
            });

            // freeze to prevent further changes
            _storeEntries = _storeEntries.ToFrozenDictionary();
            _storeCollections = _storeCollections.ToFrozenDictionary();
        }

        protected override void LoadConfiguration()
        {
            // todo handle backup file if original is corrupt
            if (File.Exists(ConfigurationFile))
            {
                using (var fs = File.OpenRead(ConfigurationFile))
                {
                    _configFile = OnionFruitConfigFile.Parser.ParseFrom(fs);
                }

                // perform config loading
                IEnumerable<Action<OnionFruitConfigFile>> loaders =
                [
                    .._storeEntries.Values.Select(x => x.SetFromConfig),
                    .._storeCollections.Values.Select(x => x.SetFromConfig)
                ];

                foreach (var loader in loaders)
                {
                    loader.Invoke(_configFile);
                }
            }
            else
            {
                _configFile = new OnionFruitConfigFile
                {
                    ConfigVersion = ConfigVersion
                };

                // write default values to config file
                foreach (var entry in _storeEntries.Values)
                {
                    entry.SetDefaultValue.Invoke(this);
                }

                foreach (var collectionEntry in _storeCollections.Values)
                {
                    collectionEntry.SetDefaultValues.Invoke(_configFile);
                }
            }

            // set client version info
            if (!string.IsNullOrEmpty(_configFile.LastClientVersion) && Version.TryParse(_configFile.LastClientVersion, out var v))
            {
                PreviousClientVersion = v;
            }

            var currentVersion = typeof(App).Assembly.GetName().Version!;
            if (PreviousClientVersion != currentVersion)
            {
                _configFile.LastClientVersion = currentVersion.ToString();
                SaveConfiguration();
            }
        }

        protected override void SaveConfiguration()
        {
            // create a backup copy of the current file
            if (File.Exists(ConfigurationFile))
            {
                File.Copy(ConfigurationFile, ConfigurationFile + ".bak", true);
            }

            using var codedOutputStream = new CodedOutputStream(File.Create(ConfigurationFile));
            _configFile.WriteTo(codedOutputStream);
        }

        private void RegisterOption<T>(OnionFruitSetting key, T defaultValue, string targetPropertyName)
        {
            var observable = RegisterOption(key, defaultValue, out var subject);

            var accessor = typeof(OnionFruitConfigFile).GetProperty(targetPropertyName);
            var accessorClearMethod = typeof(OnionFruitConfigFile).GetMethod($"Clear{targetPropertyName}");

            var accessorNullCheck = typeof(OnionFruitConfigFile).GetProperty($"Has{targetPropertyName}");

            if (accessor == null)
            {
                throw new ArgumentException("Invalid property name", nameof(targetPropertyName));
            }

            if (defaultValue == null && (accessorClearMethod == null || !accessor.PropertyType.IsClass))
            {
                throw new ArgumentException("Cannot have a null default value without a clear method", nameof(defaultValue));
            }

            // action derived on a per-config-item basis.
            // null checks are performed to ensure default values are not set.
            Action<OnionFruitConfigFile> setFromConfigAction = c =>
            {
                if ((bool?)accessorNullCheck?.GetValue(c) != false)
                {
                    subject.OnNext((T)accessor.GetValue(c, null));
                }
            };

            _storeEntries[key] = new SettingsStoreEntry(o => o.SetValue(key, defaultValue), setFromConfigAction);
            observable.ObserveOn(RxApp.TaskpoolScheduler)
                .Subscribe(value =>
                {
                    if (value == null && accessorClearMethod != null)
                    {
                        _logger.LogDebug("Configuration value {key} cleared", key);
                        accessorClearMethod.Invoke(_configFile, null);
                    }
                    else if (value == null)
                    {
                        _logger.LogDebug("Configuration value {key} reset to default value ('{val}')", key, defaultValue);
                        accessor.SetValue(_configFile, defaultValue);

                        subject.OnNext(defaultValue);
                    }
                    else
                    {
                        _logger.LogDebug("Configuration value {key} set to '{val}'", key, value);
                        accessor.SetValue(_configFile, value);
                    }

                    _ = QueueSave();
                })
                .DisposeWith(Subscriptions);
        }

        private void RegisterOption<T>(OnionFruitSetting key, T defaultValue, Func<OnionFruitConfigFile, T> getter, Action<T, OnionFruitConfigFile> setter)
        {
            var observable = RegisterOption(key, defaultValue, out var subject);

            _storeEntries[key] = new SettingsStoreEntry(o => o.SetValue(key, defaultValue), c => subject.OnNext(getter.Invoke(c)));
            observable.ObserveOn(RxApp.TaskpoolScheduler)
                .Subscribe(value =>
                {
                    _logger.LogInformation("Configuration value {key} set to '{val}'", key, value);
                    setter.Invoke(value, _configFile);

                    _ = QueueSave();
                })
                .DisposeWith(Subscriptions);
        }

        private void RegisterCollection<T>(OnionFruitSetting key, IEnumerable<T> defaultValue, Func<OnionFruitConfigFile, RepeatedField<T>> rfAccessor)
        {
            var observer = RegisterCollection<T>(key, out var list);
            _storeCollections[key] = new SettingsCollectionEntry(c => list.AddRange(rfAccessor.Invoke(c)), c =>
            {
                var collection = rfAccessor.Invoke(c);

                collection.Clear();
                collection.AddRange(defaultValue);
            });

            if (defaultValue.Any())
            {
                observer = observer.SkipInitial();
            }

            observer.ObserveOn(RxApp.TaskpoolScheduler).Subscribe(cs =>
                {
                    var collection = rfAccessor.Invoke(_configFile);

                    using (list.Connect().Bind(out var clonedList).Subscribe())
                    {
                        collection.Clear();
                        collection.AddRange(clonedList.ToList());
 
                        _logger.LogInformation("Configuration collection {key} updated ({newVals})", key, string.Join(", ", clonedList.AsEnumerable()));
                    }

                    _ = QueueSave();
                })
                .DisposeWith(Subscriptions);
        }
    }

    public enum OnionFruitSetting
    {
        TorEntryCountryCode,
        TorExitCountryCode,

        EnableWebsiteLaunchConnect,
        EnableWebsiteLaunchDisconnect,

        WebsiteLaunchConnect,
        WebsiteLaunchDisconnect,

        EnableFirewallPortRestrictions,
        AllowedFirewallPorts,

        SelectedTransportType,
        UserDefinedBridges,

        DisconnectOnTorFailure,
        EnableErrorReporting,

        EnableDiscordRpc,
        ExplicitUpdateStream
    }
}