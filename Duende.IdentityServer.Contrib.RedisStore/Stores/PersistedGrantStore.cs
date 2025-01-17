﻿using Duende.IdentityServer.Contrib.RedisStore.Extensions;
using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Stores;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Duende.IdentityServer.Contrib.RedisStore.Stores
{
    /// <summary>
    /// Provides the implementation of IPersistedGrantStore for Redis Cache.
    /// </summary>
    public class PersistedGrantStore : IPersistedGrantStore
    {
        protected readonly RedisOperationalStoreOptions options;

        protected readonly IDatabase database;

        protected readonly ILogger<PersistedGrantStore> logger;

        protected ISystemClock clock;

        public PersistedGrantStore(RedisMultiplexer<RedisOperationalStoreOptions> multiplexer, ILogger<PersistedGrantStore> logger, ISystemClock clock)
        {
            if (multiplexer is null)
                throw new ArgumentNullException(nameof(multiplexer));
            options = multiplexer.RedisOptions;
            database = multiplexer.Database;
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.clock = clock;
        }

        protected string GetKey(string key) => $"{options.KeyPrefix}{key}";

        protected string GetSetKey(string subjectId) => $"{options.KeyPrefix}{subjectId}";

        protected string GetSetKey(string subjectId, string clientId) => $"{options.KeyPrefix}{subjectId}:{clientId}";

        protected string GetSetKeyWithType(string subjectId, string clientId, string type) => $"{options.KeyPrefix}{subjectId}:{clientId}:{type}";

        protected string GetSetKeyWithSession(string subjectId, string clientId, string sessionId) => $"{options.KeyPrefix}{subjectId}:{clientId}:{sessionId}";

        public virtual async Task StoreAsync(PersistedGrant grant)
        {
            if (grant == null)
                throw new ArgumentNullException(nameof(grant));
            try
            {
                var data = ConvertToJson(grant);
                var grantKey = GetKey(grant.Key);
                var expiresIn = grant.Expiration - clock.UtcNow;
                if (!string.IsNullOrEmpty(grant.SubjectId))
                {
                    var setKeyforType = GetSetKeyWithType(grant.SubjectId, grant.ClientId, grant.Type);
                    var setKeyforSubject = GetSetKey(grant.SubjectId);
                    var setKeyforClient = GetSetKey(grant.SubjectId, grant.ClientId);
                    var setKetforSession = GetSetKeyWithSession(grant.SubjectId, grant.ClientId, grant.SessionId);

                    var ttlOfClientSet = database.KeyTimeToLiveAsync(setKeyforClient);
                    var ttlOfSubjectSet = database.KeyTimeToLiveAsync(setKeyforSubject);
                    var ttlofSessionSet = database.KeyTimeToLiveAsync(setKetforSession);

                    await Task.WhenAll(ttlOfSubjectSet, ttlOfClientSet, ttlofSessionSet);

                    var transaction = database.CreateTransaction();
                    transaction.StringSetAsync(grantKey, data, expiresIn);
                    transaction.SetAddAsync(setKeyforSubject, grantKey);
                    transaction.SetAddAsync(setKeyforClient, grantKey);
                    transaction.SetAddAsync(setKeyforType, grantKey);
                    if (!grant.SessionId.IsNullOrEmpty())
                        transaction.SetAddAsync(setKetforSession, grantKey);
                    if ((ttlOfSubjectSet.Result ?? TimeSpan.Zero) <= expiresIn)
                        transaction.KeyExpireAsync(setKeyforSubject, expiresIn);
                    if ((ttlOfClientSet.Result ?? TimeSpan.Zero) <= expiresIn)
                        transaction.KeyExpireAsync(setKeyforClient, expiresIn);
                    if (!grant.SessionId.IsNullOrEmpty() && (ttlofSessionSet.Result ?? TimeSpan.Zero) <= expiresIn)
                        transaction.KeyExpireAsync(setKetforSession, expiresIn);
                    transaction.KeyExpireAsync(setKeyforType, expiresIn);
                    await transaction.ExecuteAsync();
                }
                else
                {
                    await database.StringSetAsync(grantKey, data, expiresIn);
                }
                logger.LogDebug("grant for subject {subjectId}, clientId {clientId}, grantType {grantType} and sessionId {session} persisted successfully", grant.SubjectId, grant.ClientId, grant.Type, grant.SessionId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "exception storing persisted grant to Redis database for subject {subjectId}, clientId {clientId}, grantType {grantType} and session {sessionId}", grant.SubjectId, grant.ClientId, grant.Type, grant.SessionId);
                throw;
            }
        }

        public virtual async Task<PersistedGrant> GetAsync(string key)
        {
            try
            {
                var data = await database.StringGetAsync(GetKey(key));
                logger.LogDebug("{key} found in database: {hasValue}", key, data.HasValue);
                return data.HasValue ? ConvertFromJson(data) : null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "exception retrieving grant for key {key}", key);
                throw;
            }
        }

        public virtual async Task<IEnumerable<PersistedGrant>> GetAllAsync(PersistedGrantFilter filter)
        {
            try
            {
                var setKey = GetSetKey(filter);
                var (grants, keysToDelete) = await GetGrants(setKey);
                if (keysToDelete.Any())
                {
                    var keys = keysToDelete.ToArray();
                    var transaction = database.CreateTransaction();
                    transaction.SetRemoveAsync(GetSetKey(filter.SubjectId), keys);
                    transaction.SetRemoveAsync(GetSetKey(filter.SubjectId, filter.ClientId), keys);
                    transaction.SetRemoveAsync(GetSetKeyWithType(filter.SubjectId, filter.ClientId, filter.Type), keys);
                    transaction.SetRemoveAsync(GetSetKeyWithSession(filter.SubjectId, filter.ClientId, filter.SessionId), keys);
                    await transaction.ExecuteAsync();
                }
                logger.LogDebug("{grantsCount} persisted grants found for {subjectId}", grants.Count(), filter.SubjectId);
                return grants.Where(_ => _.HasValue).Select(_ => ConvertFromJson(_)).Where(_ => IsMatch(_, filter));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "exception while retrieving grants");
                throw;
            }
        }

        protected virtual async Task<(IEnumerable<RedisValue> grants, IEnumerable<RedisValue> keysToDelete)> GetGrants(string setKey)
        {
            var grantsKeys = await database.SetMembersAsync(setKey);
            if (!grantsKeys.Any())
                return (Enumerable.Empty<RedisValue>(), Enumerable.Empty<RedisValue>());
            var grants = await database.StringGetAsync(grantsKeys.Select(_ => (RedisKey)_.ToString()).ToArray());
            var keysToDelete = grantsKeys.Zip(grants, (key, value) => new KeyValuePair<RedisValue, RedisValue>(key, value))
                                         .Where(_ => !_.Value.HasValue).Select(_ => _.Key);
            return (grants, keysToDelete);
        }

        public virtual async Task RemoveAsync(string key)
        {
            try
            {
                var grant = await GetAsync(key);
                if (grant == null)
                {
                    logger.LogDebug("no {key} persisted grant found in database", key);
                    return;
                }
                var grantKey = GetKey(key);
                logger.LogDebug("removing {key} persisted grant from database", key);
                var transaction = database.CreateTransaction();
                transaction.KeyDeleteAsync(grantKey);
                transaction.SetRemoveAsync(GetSetKey(grant.SubjectId), grantKey);
                transaction.SetRemoveAsync(GetSetKey(grant.SubjectId, grant.ClientId), grantKey);
                transaction.SetRemoveAsync(GetSetKeyWithType(grant.SubjectId, grant.ClientId, grant.Type), grantKey);
                transaction.SetRemoveAsync(GetSetKeyWithSession(grant.SubjectId, grant.ClientId, grant.SessionId), grantKey);
                await transaction.ExecuteAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "exception removing {key} persisted grant from database", key);
                throw;
            }

        }

        public virtual async Task RemoveAllAsync(PersistedGrantFilter filter)
        {
            try
            {
                filter.Validate();
                var setKey = GetSetKey(filter);
                var grants = await database.SetMembersAsync(setKey);
                logger.LogDebug("removing {grantKeysCount} persisted grants from database for subject {subjectId}, clientId {clientId}, grantType {type} and session {session}", grants.Count(), filter.SubjectId, filter.ClientId, filter.Type, filter.SessionId);
                if (!grants.Any()) return;
                var transaction = database.CreateTransaction();
                transaction.KeyDeleteAsync(grants.Select(_ => (RedisKey)_.ToString()).Concat(new RedisKey[] { setKey }).ToArray());
                transaction.SetRemoveAsync(GetSetKey(filter.SubjectId), grants);
                transaction.SetRemoveAsync(GetSetKey(filter.SubjectId, filter.ClientId), grants);
                transaction.SetRemoveAsync(GetSetKeyWithType(filter.SubjectId, filter.ClientId, filter.Type), grants);
                transaction.SetRemoveAsync(GetSetKeyWithSession(filter.SubjectId, filter.ClientId, filter.SessionId), grants);
                await transaction.ExecuteAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "exception removing persisted grants from database for subject {subjectId}, clientId {clientId}, grantType {type} and session {session}", filter.SubjectId, filter.ClientId, filter.Type, filter.SessionId);
                throw;
            }
        }

        protected virtual string GetSetKey(PersistedGrantFilter filter) =>
            (!filter.ClientId.IsNullOrEmpty(), !filter.SessionId.IsNullOrEmpty(), !filter.Type.IsNullOrEmpty()) switch
            {
                (true, true, false) => GetSetKeyWithSession(filter.SubjectId, filter.ClientId, filter.SessionId),
                (true, _, false) => GetSetKey(filter.SubjectId, filter.ClientId),
                (true, _, true) => GetSetKeyWithType(filter.SubjectId, filter.ClientId, filter.Type),
                _ => GetSetKey(filter.SubjectId),
            };

        protected bool IsMatch(PersistedGrant grant, PersistedGrantFilter filter)
        {
            return (filter.SubjectId.IsNullOrEmpty() ? true : grant.SubjectId == filter.SubjectId)
                && (filter.ClientId.IsNullOrEmpty() ? true : grant.ClientId == filter.ClientId)
                && (filter.SessionId.IsNullOrEmpty() ? true : grant.SessionId == filter.SessionId)
                && (filter.Type.IsNullOrEmpty() ? true : grant.Type == filter.Type);
        }

        #region Json
        protected static string ConvertToJson(PersistedGrant grant)
        {
            return JsonSerializer.Serialize(grant);
        }

        protected static PersistedGrant ConvertFromJson(string data)
        {
            return JsonSerializer.Deserialize<PersistedGrant>(data);
        }
        #endregion
    }
}