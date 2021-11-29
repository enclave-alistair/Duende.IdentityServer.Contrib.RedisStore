using Duende.IdentityServer.Contrib.RedisStore.Extensions;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Stores.Serialization;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Duende.IdentityServer.Contrib.RedisStore.Cache
{
    /// <summary>
    /// Redis based implementation for <see cref="ICache{T}"/>.
    /// </summary>
    /// <typeparam name="T">The cached type.</typeparam>
    internal class RedisCache<T> : ICache<T> where T : class
    {
        private readonly IDatabase database;

        private readonly RedisCacheOptions options;

        private readonly ILogger<RedisCache<T>> logger;

        public RedisCache(RedisMultiplexer<RedisCacheOptions> multiplexer, ILogger<RedisCache<T>> logger)
        {
            if (multiplexer is null)
                throw new ArgumentNullException(nameof(multiplexer));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

            options = multiplexer.RedisOptions;
            database = multiplexer.Database;
        }

        private string GetKey(string key) => $"{options.KeyPrefix}{typeof(T).FullName}:{key}";

        public async Task<T> GetAsync(string key)
        {
            var cacheKey = GetKey(key);
            var item = await database.StringGetAsync(cacheKey);
            if (item.HasValue)
            {
                logger.LogDebug("retrieved {type} with Key: {key} from Redis Cache successfully.", typeof(T).FullName, key);
                return Deserialize(item);
            }
            else
            {
                logger.LogDebug("missed {type} with Key: {key} from Redis Cache.", typeof(T).FullName, key);
                return default;
            }
        }

        public async Task SetAsync(string key, T item, TimeSpan expiration)
        {
            var cacheKey = GetKey(key);
            await database.StringSetAsync(cacheKey, Serialize(item), expiration);
            logger.LogDebug("persisted {type} with Key: {key} in Redis Cache successfully.", typeof(T).FullName, key);
        }

        #region Json
        private JsonSerializerOptions SerializerSettings
        {
            get
            {
                var settings = new JsonSerializerOptions();
                settings.Converters.Add(new ClaimConverter());
                return settings;
            }
        }

        private T Deserialize(string json)
        {
            return JsonSerializer.Deserialize<T>(json, SerializerSettings);
        }

        private string Serialize(T item)
        {
            return JsonSerializer.Serialize(item, SerializerSettings);
        }
        #endregion
    }
}
