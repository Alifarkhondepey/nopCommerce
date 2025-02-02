﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using Nito.AsyncEx;
using Nop.Core.Configuration;

namespace Nop.Core.Caching
{
    /// <summary>
    /// Represents a distributed cache 
    /// </summary>
    public partial class DistributedCacheManager : CacheKeyService, ILocker, IStaticCacheManager
    {
        #region Fields

        private readonly IDistributedCache _distributedCache;
        private readonly ConcurrentDictionary<CacheKey, object> _items;
        private static readonly List<string> _keys;
        private static readonly AsyncLock _locker;

        #endregion

        #region Ctor

        static DistributedCacheManager()
        {
            _locker = new AsyncLock();
            _keys = new List<string>();
        }

        public DistributedCacheManager(AppSettings appSettings, IDistributedCache distributedCache) :base(appSettings)
        {
            _distributedCache = distributedCache;
            _items = new ConcurrentDictionary<CacheKey, object>();
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Prepare cache entry options for the passed key
        /// </summary>
        /// <param name="key">Cache key</param>
        /// <returns>Cache entry options</returns>
        private DistributedCacheEntryOptions PrepareEntryOptions(CacheKey key)
        {
            //set expiration time for the passed cache key
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(key.CacheTime)
            };

            return options;
        }

        /// <summary>
        /// Try to get the cached item
        /// </summary>
        /// <typeparam name="T">Type of cached item</typeparam>
        /// <param name="key">Cache key</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the flag which indicate is the key exists in the cache, cached item or default value
        /// </returns>
        private async Task<(bool isSet, T item)> TryGetItemAsync<T>(CacheKey key)
        {
            var json = await _distributedCache.GetStringAsync(key.Key);

            if (string.IsNullOrEmpty(json))
                return (false, default);
            
            using var _ = await _locker.LockAsync();
            if (!_keys.Contains(key.Key))
                _keys.Add(key.Key);

            return (true, JsonConvert.DeserializeObject<T>(json));
        }

        /// <summary>
        /// Try to get the cached item
        /// </summary>
        /// <typeparam name="T">Type of cached item</typeparam>
        /// <param name="key">Cache key</param>
        /// <returns>Flag which indicate is the key exists in the cache, cached item or default value</returns>
        private (bool isSet, T item) TryGetItem<T>(CacheKey key)
        {
            var json = _distributedCache.GetString(key.Key);

            if (string.IsNullOrEmpty(json))
                return (false, default);

            using var _ = _locker.Lock();
            if (!_keys.Contains(key.Key))
                _keys.Add(key.Key);

            return (true, JsonConvert.DeserializeObject<T>(json));
        }

        /// <summary>
        /// Add the specified key and object to the cache
        /// </summary>
        /// <param name="key">Key of cached item</param>
        /// <param name="data">Value for caching</param>
        private void Set(CacheKey key, object data)
        {
            if ((key?.CacheTime ?? 0) <= 0 || data == null)
                return;

            _distributedCache.SetString(key.Key, JsonConvert.SerializeObject(data), PrepareEntryOptions(key));
            _items.TryAdd(key, data);

            using var _ = _locker.Lock();
            _keys.Add(key.Key);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Performs application-defined tasks associated with freeing,
        /// releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Get a cached item. If it's not in the cache yet, then load and cache it
        /// </summary>
        /// <typeparam name="T">Type of cached item</typeparam>
        /// <param name="key">Cache key</param>
        /// <param name="acquire">Function to load item if it's not in the cache yet</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the cached value associated with the specified key
        /// </returns>
        public async Task<T> GetAsync<T>(CacheKey key, Func<Task<T>> acquire)
        {
            //little performance workaround here:
            //we use local dictionary to cache a loaded object in memory for the current HTTP request.
            //this way we won't connect to distributed cache server many times per HTTP request (e.g. each time to load a locale or setting)
            if (_items.ContainsKey(key))
                return (T)_items.GetOrAdd(key, acquire);

            if (key.CacheTime <= 0)
                return await acquire();

            var (isSet, item) = await TryGetItemAsync<T>(key);

            if (isSet)
            {
                if (item != null)
                    _items.TryAdd(key, item);

                return item;
            }

            var result = await acquire();

            if (result != null)
                await SetAsync(key, result);

            return result;
        }

        /// <summary>
        /// Get a cached item. If it's not in the cache yet, then load and cache it
        /// </summary>
        /// <typeparam name="T">Type of cached item</typeparam>
        /// <param name="key">Cache key</param>
        /// <param name="acquire">Function to load item if it's not in the cache yet</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the cached value associated with the specified key
        /// </returns>
        public async Task<T> GetAsync<T>(CacheKey key, Func<T> acquire)
        {
            //little performance workaround here:
            //we use local dictionary to cache a loaded object in memory for the current HTTP request.
            //this way we won't connect to distributed cache server many times per HTTP request (e.g. each time to load a locale or setting)
            if (_items.ContainsKey(key))
                return (T)_items.GetOrAdd(key, acquire);

            if (key.CacheTime <= 0)
                return acquire();

            var (isSet, item) = await TryGetItemAsync<T>(key);

            if (isSet)
            {
                if (item != null)
                    _items.TryAdd(key, item);

                return item;
            }

            var result = acquire();

            if (result != null)
                await SetAsync(key, result);

            return result;
        }

        /// <summary>
        /// Get a cached item. If it's not in the cache yet, then load and cache it
        /// </summary>
        /// <typeparam name="T">Type of cached item</typeparam>
        /// <param name="key">Cache key</param>
        /// <param name="acquire">Function to load item if it's not in the cache yet</param>
        /// <returns>The cached value associated with the specified key</returns>
        public T Get<T>(CacheKey key, Func<T> acquire)
        {
            //little performance workaround here:
            //we use local dictionary to cache a loaded object in memory for the current HTTP request.
            //this way we won't connect to distributed cache server many times per HTTP request (e.g. each time to load a locale or setting)
            if (_items.ContainsKey(key))
                return (T)_items.GetOrAdd(key, acquire);

            if (key.CacheTime <= 0)
                return acquire();

            var (isSet, item) = TryGetItem<T>(key);

            if (isSet)
            { 
                if (item != null)
                    _items.TryAdd(key, item);

                return item;
            }

            var result = acquire();

            if (result != null)
                Set(key, result);

            return result;
        }

        /// <summary>
        /// Remove the value with the specified key from the cache
        /// </summary>
        /// <param name="cacheKey">Cache key</param>
        /// <param name="cacheKeyParameters">Parameters to create cache key</param>
        /// <returns>A task that represents the asynchronous operation</returns>
        public async Task RemoveAsync(CacheKey cacheKey, params object[] cacheKeyParameters)
        {
            cacheKey = PrepareKey(cacheKey, cacheKeyParameters);

            await _distributedCache.RemoveAsync(cacheKey.Key);
            _items.TryRemove(cacheKey, out var _);

            using var _ = await _locker.LockAsync();
            _keys.Remove(cacheKey.Key);
        }

        /// <summary>
        /// Add the specified key and object to the cache
        /// </summary>
        /// <param name="key">Key of cached item</param>
        /// <param name="data">Value for caching</param>
        /// <returns>A task that represents the asynchronous operation</returns>
        public async Task SetAsync(CacheKey key, object data)
        {
            if ((key?.CacheTime ?? 0) <= 0 || data == null)
                return;

            await _distributedCache.SetStringAsync(key.Key, JsonConvert.SerializeObject(data), PrepareEntryOptions(key));
            _items.TryAdd(key, data);

            using var _ = await _locker.LockAsync();
            _keys.Add(key.Key);
        }

        /// <summary>
        /// Remove items by cache key prefix
        /// </summary>
        /// <param name="prefix">Cache key prefix</param>
        /// <param name="prefixParameters">Parameters to create cache key prefix</param>
        /// <returns>A task that represents the asynchronous operation</returns>
        public async Task RemoveByPrefixAsync(string prefix, params object[] prefixParameters)
        {
            using var _ = await _locker.LockAsync();

            prefix = PrepareKeyPrefix(prefix, prefixParameters);

            var regex = new Regex(prefix,
                RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var matchesKeys = new List<CacheKey>();

            //get cache keys that matches pattern
            matchesKeys.AddRange(_items.Keys.Where(key => regex.IsMatch(key.Key)).ToList());

            //remove matching values
            if (matchesKeys.Any())
                foreach (var key in matchesKeys)
                    _items.TryRemove(key, out var _);

            foreach (var key in _keys.Where(key => key.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase)).ToList())
            {
                await _distributedCache.RemoveAsync(key);
                _keys.Remove(key);
            }
        }

        /// <summary>
        /// Clear all cache data
        /// </summary>
        /// <returns>A task that represents the asynchronous operation</returns>
        public async Task ClearAsync()
        {
            using var _ = await _locker.LockAsync();

            foreach (var key in _keys)
                await _distributedCache.RemoveAsync(key);

            _items.Clear();
            _keys.Clear();
        }

        /// <summary>
        /// Perform asynchronous action with exclusive in-memory lock
        /// </summary>
        /// <param name="resource">The key we are locking on</param>
        /// <param name="expirationTime">The time after which the lock will automatically be expired</param>
        /// <param name="action">Action to be performed with locking</param>
        /// <returns>True if lock was acquired and action was performed; otherwise false</returns>
        public async Task<bool> PerformActionWithLockAsync(string resource, TimeSpan expirationTime, Func<Task> action)
        {
            if (!string.IsNullOrEmpty(await _distributedCache.GetStringAsync(resource)))
                return false;

            try
            {
                await _distributedCache.SetStringAsync(resource, resource, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = expirationTime
                });

                //perform action
                await action();

                return true;
            }
            finally
            {
                //release lock even if action fails
                await _distributedCache.RemoveAsync(resource);
            }
        }

        #endregion
    }
}
