// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Common.Extensions;
using EdFi.Ods.Api.IdentityValueMappers;
using EdFi.Ods.Common.Caching;
using EdFi.Ods.Common.Context;
using EdFi.Ods.Common.Extensions;
using EdFi.Ods.Common.Providers;
using EdFi.Ods.Common.Specifications;
using log4net;
using Microsoft.Extensions.Caching.Memory;

namespace EdFi.Ods.Api.Caching
{
    public class PersonUniqueIdToUsiCache : IPersonUniqueIdToUsiCache
    {
        private const string CacheKeyPrefix = "IdentityValueMaps_";

        /// <summary>
        /// Gets or sets a static delegate to obtain the cache.
        /// </summary>
        /// <remarks>This method exists to serve the cache to the NHibernate-generated entities in a way
        /// that does not require IoC component resolution, for performance reasons.</remarks>
        public static Func<IPersonUniqueIdToUsiCache> GetCache = () => null;
        private readonly ICacheProvider _cacheProvider;
        private readonly IEdFiOdsInstanceIdentificationProvider _edFiOdsInstanceIdentificationProvider;

        private readonly object _identityValueMapsLock = new object();

        private readonly ILog _logger = LogManager.GetLogger(typeof(PersonUniqueIdToUsiCache));
        private readonly IPersonIdentifiersProvider _personIdentifiersProvider;
        private readonly bool _synchronousInitialization;
        private readonly IUniqueIdToUsiValueMapper _uniqueIdToUsiValueMapper;

        private readonly TimeSpan _slidingExpiration;
        private readonly TimeSpan _absoluteExpirationPeriod;

        /// <summary>
        /// Provides cached translations between UniqueIds and USI values.
        /// </summary>
        /// <param name="cacheProvider">The cache where the database-specific maps (dictionaries) are stored, expiring after 4 hours of inactivity.</param>
        /// <param name="edFiOdsInstanceIdentificationProvider">Identifies the ODS instance for the current call.</param>
        /// <param name="uniqueIdToUsiValueMapper">A component that maps between USI and UniqueId values.</param>
        /// <param name="personIdentifiersProvider">A component that retrieves all Person identifiers.</param>
        /// <param name="slidingExpiration">Indicates how long the cache values will remain in memory after being used before all the cached values are removed.</param>
        /// <param name="absoluteExpirationPeriod">Indicates the maximum time that the cache values will remain in memory before being refreshed.</param>
        /// <param name="synchronousInitialization">Indicates whether the cache should wait until all the Person identifiers are loaded before responding, or if using the value mappers initially to avoid an initial delay is preferable.</param>
        public PersonUniqueIdToUsiCache(
            ICacheProvider cacheProvider,
            IEdFiOdsInstanceIdentificationProvider edFiOdsInstanceIdentificationProvider,
            IUniqueIdToUsiValueMapper uniqueIdToUsiValueMapper,
            IPersonIdentifiersProvider personIdentifiersProvider,
            TimeSpan slidingExpiration,
            TimeSpan absoluteExpirationPeriod,
            bool synchronousInitialization)
        {
            _cacheProvider = cacheProvider;
            _edFiOdsInstanceIdentificationProvider = edFiOdsInstanceIdentificationProvider;
            _uniqueIdToUsiValueMapper = uniqueIdToUsiValueMapper;
            _personIdentifiersProvider = personIdentifiersProvider;
            _synchronousInitialization = synchronousInitialization;

            if (slidingExpiration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(slidingExpiration), "TimeSpan cannot be a negative value.");
            }

            if (absoluteExpirationPeriod < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(absoluteExpirationPeriod), "TimeSpan cannot be a negative value.");
            }

            // Use sliding expiration value, if both are set.
            if (slidingExpiration > TimeSpan.Zero && absoluteExpirationPeriod > TimeSpan.Zero)
            {
                absoluteExpirationPeriod = TimeSpan.Zero;
            }

            _slidingExpiration = slidingExpiration;
            _absoluteExpirationPeriod = absoluteExpirationPeriod;
        }

        /// <summary>
        /// Gets or sets the optional dependency for when the cache is being used in the scope of an HttpContext and
        /// specific context values (see <see cref="IHttpContextStorageTransferKeys"/>) must be transferred to CallContext for
        /// a worker thread to perform background initialization of the cache.
        /// </summary>
        public IHttpContextStorageTransfer HttpContextStorageTransfer { get; set; }

        /// <summary>
        /// Gets the externally defined UniqueId for the specified type of person and the ODS-specific surrogate identifier.
        /// </summary>
        /// <param name="personType">The type of the person (e.g. Staff, Student, Parent).</param>
        /// <param name="usi">The integer-based identifier for the specified representation of the person,
        /// specific to a particular ODS database instance.</param>
        /// <returns>The UniqueId value assigned to the person if found; otherwise <b>null</b>.</returns>
        public string GetUniqueId(string personType, int usi)
        {
            if (usi == default)
            {
                return default;
            }

            // Get the cache first, initializing it if necessary
            string odsContext = GetOdsSpecificContext();
            var uniqueIdByUsi = GetUniqueIdByUsiMap(personType, odsContext);

            // Check the dictionary for the value
            if (uniqueIdByUsi != null && uniqueIdByUsi.TryGetValue(usi, out string cachedUniqueId))
            {
                return cachedUniqueId;
            }

            // Call the value mapper for the individual value
            var lookupUniqueId = LookupUniqueId(personType, usi);

            // Save the value (if found and we have somewhere to store it)
            if (lookupUniqueId != null && uniqueIdByUsi != null)
            {
                uniqueIdByUsi.TryAdd(usi, lookupUniqueId);

                GetUsiByUniqueIdMap(personType, odsContext)
                   .AddOrUpdate(lookupUniqueId, usi, (x, y) => usi);
            }

            return lookupUniqueId;
        }

        /// <summary>
        /// Gets the ODS-specific integer identifier for the specified type of person and their UniqueId value.
        /// </summary>
        /// <param name="personType">The type of the person (e.g. Staff, Student, Parent).</param>
        /// <param name="uniqueId">The UniqueId value associated with the person.</param>
        /// <returns>The ODS-specific integer identifier for the specified type of representation of
        /// the person if found; otherwise 0.</returns>
        public int GetUsi(string personType, string uniqueId)
        {
            var usi = GetUsi(personType, uniqueId, false);
            return usi.GetValueOrDefault();
        }

        /// <summary>
        /// Gets the ODS-specific integer identifier for the specified type of person and their UniqueId value.
        /// </summary>
        /// <param name="personType">The type of the person (e.g. Staff, Student, Parent).</param>
        /// <param name="uniqueId">The UniqueId value associated with the person.</param>
        /// <returns>The ODS-specific integer identifier for the specified type of representation of
        /// the person if found; otherwise <b>null</b>.</returns>
        public int? GetUsiNullable(string personType, string uniqueId)
        {
            var usi = GetUsi(personType, uniqueId, true);

            return usi is default(int) ? null : usi;
        }

        private ConcurrentDictionary<int, string> GetUniqueIdByUsiMap(string personType, string context)
        {
            if (!TryGetIdentityValueMaps(personType, context, out IdentityValueMaps identityValueMaps))
            {
                return null;
            }

            return identityValueMaps.UniqueIdByUsi;
        }

        private ConcurrentDictionary<string, int> GetUsiByUniqueIdMap(string personType, string context)
        {
            if (!TryGetIdentityValueMaps(personType, context, out IdentityValueMaps identityValueMaps))
            {
                return null;
            }

            return identityValueMaps.UsiByUniqueId;
        }

        private bool TryGetIdentityValueMaps(string personType, string context, out IdentityValueMaps identityValueMaps)
        {
            string cacheKey = GetPersonTypeIdentifiersCacheKey(personType, context);

            if (!_cacheProvider.TryGetCachedObject(cacheKey, out object personCacheAsObject))
            {
                // Make sure there is only one cache set being initialized at a time
                lock (_identityValueMapsLock)
                {
                    // Make sure that the entry still doesn't exist yet
                    if (!_cacheProvider.TryGetCachedObject(cacheKey, out personCacheAsObject))
                    {
                        var identityCacheEntry = new IdentityCacheEntry();

                        // Put the initialization task on the cached object so that we know the initialization status by cache entry key
                        // Even if/when the cache provider storage changes context
                        identityCacheEntry.InitializationTask = InitializePersonTypeValueMaps(cacheKey, identityCacheEntry, personType);

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"Inserting USI/Unique identifiers cache entry for '{cacheKey}'.");
                        }
                        
                        // Initial Insert is for while async initialization is running.
                        _cacheProvider.Insert(cacheKey, identityCacheEntry, DateTime.MaxValue, TimeSpan.FromMinutes(5));

                        _cacheProvider.TryGetCachedObject(cacheKey, out personCacheAsObject);
                    }
                }
            }

            var personCacheEntry = personCacheAsObject as IdentityCacheEntry;

            if (personCacheEntry == null)
            {
                identityValueMaps = null;
                return false;
            }

            if (_synchronousInitialization
                && (personCacheEntry.IdentityValueMaps.UniqueIdByUsi == null
                    || personCacheEntry.IdentityValueMaps.UsiByUniqueId == null))
            {
                // Wait for the initialization task to complete
                personCacheEntry.InitializationTask.ConfigureAwait(false).GetAwaiter().GetResult();

                // If initialization failed, return false.
                if (personCacheEntry.IdentityValueMaps?.UniqueIdByUsi == null
                    || personCacheEntry.IdentityValueMaps?.UsiByUniqueId == null)
                {
                    identityValueMaps = null;
                    return false;
                }

                // With initialization complete, try again (using a single recursive call)
                return TryGetIdentityValueMaps(personType, context, out identityValueMaps);
            }

            identityValueMaps = personCacheEntry.IdentityValueMaps;
            return true;
        }

        private Task InitializePersonTypeValueMaps(string cacheKey, IdentityCacheEntry entry, string personType)
        {
            // Validate Person type
            if (!PersonEntitySpecification.IsPersonEntity(personType))
            {
                throw new ArgumentException(
                    string.Format(
                        "Invalid person type '{0}'. Valid person types are: {1}",
                        personType,
                        "'" + string.Join("','", PersonEntitySpecification.ValidPersonTypes) + "'"));
            }

            // In web application scenarios, copy pertinent context from HttpContext to CallContext
            HttpContextStorageTransfer?.TransferContext();

            // TODO: GKM - Questions... Why is this not Task.Run ? The point is that it should be on a background thread and context transferred.
            // But there is no context being transferred anymore. Why not?
            var task = InitializePersonTypeValueMapsAsync(cacheKey, entry, personType);

            // Looks like a total hack because we're not controlling the narrative around this definitively background work
            if (task.Status == TaskStatus.Created)
            {
                task.Start();
            }

            return task;
        }

        private async Task InitializePersonTypeValueMapsAsync(string cacheKey, IdentityCacheEntry entry, string personType)
        {
            // Un-handled exceptions here will take down the ASP.NET process
            try
            {
                // Start building the data
                Stopwatch stopwatch = null;

                if (_logger.IsDebugEnabled)
                {
                    stopwatch = new Stopwatch();
                    stopwatch.Start();
                }

                var personIdentifiers = await _personIdentifiersProvider.GetAllPersonIdentifiers(personType);

                var uniqueIdByUsi = new ConcurrentDictionary<int, string>(Environment.ProcessorCount, personIdentifiers.Count);
                var usiByUniqueId = new ConcurrentDictionary<string, int>(Environment.ProcessorCount, personIdentifiers.Count);

                foreach (var valueMap in personIdentifiers)
                {
                    uniqueIdByUsi.TryAdd(valueMap.Usi, valueMap.UniqueId);
                    usiByUniqueId.TryAdd(valueMap.UniqueId, valueMap.Usi);
                }

                if (_logger.IsDebugEnabled)
                {
                    stopwatch.Stop();

                    _logger.DebugFormat(
                        "UniqueId/USI cache for {0} initialized {1:n0} entries in {2:n0} milliseconds.",
                        personType,
                        uniqueIdByUsi.Count,
                        stopwatch.ElapsedMilliseconds);
                }

                entry.IdentityValueMaps = new IdentityValueMaps(uniqueIdByUsi, usiByUniqueId);

                // Now that it's loaded extend the cache expiration.
                _cacheProvider.Insert(cacheKey, entry, GetAbsoluteExpiration(), _slidingExpiration);
                
                // Drop the reference to the initialization task
                entry.InitializationTask = null;
            }
            catch (Exception ex)
            {
                _logger.Error(
                    "An exception occurred while trying to warm the PersonCache. UniqueIds will be retrieved individually.",
                    ex);
            }
        }

        private DateTime GetAbsoluteExpiration() => _absoluteExpirationPeriod == TimeSpan.Zero
            ? DateTime.MaxValue
            : DateTime.UtcNow.Add(_absoluteExpirationPeriod);

        private static string GetPersonTypeIdentifiersCacheKey(string personType, string context)
        {
            return string.Concat(CacheKeyPrefix, personType, "_", context);
        }

        private string GetUniqueIdByUsiCacheKey(string personType, int usi)
        {
            return GetUniqueIdByUsiCacheKey(personType, usi, GetOdsSpecificContext());
        }

        private string GetUniqueIdByUsiCacheKey(string personType, int usi, string context)
        {
            return string.Concat(personType, "_", usi, "_", context);
        }

        private string LookupUniqueId(string personTypeName, int usi)
        {
            return _uniqueIdToUsiValueMapper.GetUniqueId(personTypeName, usi)?.UniqueId;
        }

        private int LookupUsi(string personTypeName, string uniqueId)
        {
            return _uniqueIdToUsiValueMapper.GetUsi(personTypeName, uniqueId)?.Usi ?? default;
        }

        private int? GetUsi(string personType, string uniqueId, bool isNullable)
        {
            if (string.IsNullOrWhiteSpace(uniqueId))
            {
                return isNullable
                    ? default(int?)
                    : default(int);
            }

            // Get the cache first, initializing it if necessary
            string context = GetOdsSpecificContext();
            var usiByUniqueId = GetUsiByUniqueIdMap(personType, context);

            _logger.DebugFormat(
                "For person type: {0}, there are {1} records cached.",
                personType,
                usiByUniqueId?.Count ?? 0);

            // Check the cache for the value
            if (usiByUniqueId != null && usiByUniqueId.TryGetValue(uniqueId, out int cachedUsi))
            {
                if (cachedUsi != default(int))
                {
                    return cachedUsi;
                }
            }

            int lookupUsi = LookupUsi(personType, uniqueId);

            // Save the value
            if (usiByUniqueId != null && lookupUsi != default(int))
            {
                usiByUniqueId.TryAdd(uniqueId, lookupUsi);

                GetUniqueIdByUsiMap(personType, context)
                   .AddOrUpdate(lookupUsi, uniqueId, (x, y) => uniqueId);
            }

            return lookupUsi;
        }
        
        private string GetOdsSpecificContext()
        {
            return string.Concat("from_", _edFiOdsInstanceIdentificationProvider.GetInstanceIdentification());
        }

        private class IdentityCacheEntry
        {
            public Task InitializationTask { get; set; }

            public IdentityValueMaps IdentityValueMaps { get; set; } = new IdentityValueMaps();
        }
        
        private class IdentityValueMaps
        {
            public IdentityValueMaps() { }
            
            public IdentityValueMaps(ConcurrentDictionary<int, string> uniqueIdByUsi, ConcurrentDictionary<string, int> usiByUniqueId)
            {
                UniqueIdByUsi = uniqueIdByUsi;
                UsiByUniqueId = usiByUniqueId;
            }
            
            public ConcurrentDictionary<int, string> UniqueIdByUsi { get; }

            public ConcurrentDictionary<string, int> UsiByUniqueId { get; }
        }
    }
}
