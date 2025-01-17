﻿using StackExchange.Redis;

namespace Duende.IdentityServer.Contrib.RedisStore.Extensions
{
    /// <summary>
    /// represents Redis general multiplexer
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class RedisMultiplexer<T> where T : RedisOptions
    {
        public RedisMultiplexer(T redisOptions)
        {
            RedisOptions = redisOptions;
            GetDatabase();
        }

        private void GetDatabase()
        {
            Database = RedisOptions.Multiplexer.GetDatabase(string.IsNullOrEmpty(RedisOptions.RedisConnectionString) ? -1 : RedisOptions.Db);
        }

        internal T RedisOptions { get; }

        internal IDatabase Database { get; private set; }
    }
}
