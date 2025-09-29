/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.IO;
using QuantConnect.Interfaces;
using QuantConnect;
using QuantConnect.Util;
using Microsoft.Data.Sqlite;
using QuantConnect.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Ionic.Zip;
using QuantConnect.Util;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Provides an implementation of <see cref="IDataCacheProvider"/> that uses a temporary SQLite database for caching.
    /// The cache is shared across processes and is destroyed when the provider is disposed.
    /// </summary>
    public class SqliteDataCacheProvider : IDataCacheProvider
    {
        private long _disposed;
        private readonly object _dbLock = new object();
        private readonly string _sharedCacheName;
        private readonly SqliteConnection _connection;
        private readonly IDataProvider _dataProvider;

        /// <summary>
        /// Property indicating the data is temporary in nature and should not be cached.
        /// </summary>
        public bool IsDataEphemeral { get; } = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqliteDataCacheProvider"/> class.
        /// </summary>
        /// <param name="dataProvider">The data provider to use for fetching data on cache misses.</param>
        public SqliteDataCacheProvider(IDataProvider dataProvider)
        {
            _dataProvider = dataProvider;
            // Use a shared in-memory database for maximum performance.
            // The database will exist as long as one process holds a connection.
            _sharedCacheName = "lean_cache";
            
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _sharedCacheName,
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            _connection = new SqliteConnection(connectionString);
            _connection.Open();

            // Enable Write-Ahead Logging for better concurrency
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode = WAL;";
                command.ExecuteNonQuery();
            }

            // Create the cache table if it doesn't exist
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS CacheData (
                        Key TEXT PRIMARY KEY,
                        Content BLOB
                    );";
                command.ExecuteNonQuery();
            }

            Log.Trace($"SqliteDataCacheProvider initialized with in-memory shared cache: '{_sharedCacheName}'");
        }

        /// <summary>
        /// Fetches data from the cache. If not found, fetches from the data provider and stores it in the cache.
        /// </summary>
        /// <param name="key">The key of the data to fetch.</param>
        /// <returns>A stream of the data, or null if not found.</returns>
        public Stream Fetch(string key)
        {
            // First, try to fetch from cache without locking
            var cachedStream = TryFetchFromDb(key);
            if (cachedStream != null)
            {
                return cachedStream;
            }

            // Cache miss, now we need to synchronize across processes
            var mutexName = GetMutexName(key);
            using (var mutex = new Mutex(false, mutexName))
            {
                try
                {
                    mutex.WaitOne();

                    // Double-check cache after acquiring the lock
                    cachedStream = TryFetchFromDb(key);
                    if (cachedStream != null)
                    {
                        return cachedStream;
                    }

                    // Still a miss, this thread will perform the fetch and store
                    return FetchAndStore(key);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        /// <summary>
        /// Stores data in the cache.
        /// </summary>
        /// <param name="key">The key of the data to store.</param>
        /// <param name="data">The data to store.</param>
        public void Store(string key, byte[] data)
        {
            lock (_dbLock)
            {
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "INSERT OR REPLACE INTO CacheData (Key, Content) VALUES (@Key, @Content);";
                    command.Parameters.AddWithValue("@Key", key);
                    command.Parameters.AddWithValue("@Content", data);
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Returns a list of zip entries in a provided zip file by reading the file directly.
        /// This implementation does not use the cache for listing entries.
        /// </summary>
        public List<string> GetZipEntries(string zipFile)
        {
            using (var stream = _dataProvider.Fetch(zipFile))
            {
                if (stream == null)
                {
                    throw new ArgumentException($"Failed to get zip entries from {zipFile}, file not found.");
                }

                try
                {
                    using (var zip = ZipFile.Read(stream))
                    {
                        return zip.Entries.Select(entry => entry.FileName).ToList();
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, $"SqliteDataCacheProvider.GetZipEntries(): Corrupt zip file: {zipFile}");
                    throw new ArgumentException($"Failed to get zip entries from corrupt zip file {zipFile}", e);
                }
            }
        }

        /// <summary>
        /// Disposes of the cache provider, closing the database connection and deleting the database file.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
            {
                return;
            }

            // Disposing the connection will also close it. We add a null-conditional for robustness.
            _connection?.Dispose();
            
            // For in-memory shared databases, clearing the pool is important to allow the
            // database to be fully cleaned up once all connections are closed.
            SqliteConnection.ClearAllPools();
        }

        private Stream TryFetchFromDb(string key)
        {
            lock (_dbLock)
            {
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "SELECT Content FROM CacheData WHERE Key = @Key;";
                    command.Parameters.AddWithValue("@Key", key);

                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            var stream = reader.GetStream(0);
                            var memoryStream = new MemoryStream();
                            stream.CopyTo(memoryStream);
                            memoryStream.Position = 0;
                            return memoryStream;
                        }
                    }
                }
            }
            return null;
        }

        private Stream FetchAndStore(string key)
        {
            LeanData.ParseKey(key, out var filename, out var entryName);

            if (!filename.EndsWith(".zip", StringComparison.InvariantCultureIgnoreCase) || string.IsNullOrEmpty(entryName))
            {
                return _dataProvider.Fetch(key);
            }

            using (var stream = _dataProvider.Fetch(filename))
            {
                if (stream == null) return null;

                try
                {
                    using (var zip = ZipFile.Read(stream))
                    {
                        var zipEntry = zip[entryName];
                        if (zipEntry == null) return null;

                        using (var entryStream = new MemoryStream())
                        {
                            zipEntry.Extract(entryStream);
                            var decompressedData = entryStream.ToArray();
                            
                            Store(key, decompressedData);

                            Store(key, decompressedData);

                            // We must return a NEW stream, because the 'entryStream' will be disposed by the using block.
                            // This was the cause of the "first run fails, second run succeeds" bug.
                            return new MemoryStream(decompressedData);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, $"SqliteDataCacheProvider.FetchAndStore(): Corrupt zip file or entry: {filename}#{entryName}");
                    return null;
                }
            }
        }

        private static string GetMutexName(string key)
        {
            // Mutex names have length and character limitations.
            // We hash the key to create a valid and unique name.
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
                return $"Global\\LeanCache_{Convert.ToBase64String(hash).Replace("=", "").Replace("/", "_")}";
            }
        }
    }
}