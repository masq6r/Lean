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
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Provides an implementation of <see cref="IDataCacheProvider"/> that uses a memory-mapped file for cross-process caching.
    /// The cache is shared across all processes using it and persists until the system is rebooted or the file is deleted.
    /// </summary>
    public class MemoryMappedFileCacheProvider : IDataCacheProvider
    {
        private const long MagicNumber = 0xDEADBEEF;
        private const string CacheName = "LeanSharedCache";
        private const int IndexSize = 100000; // Number of index entries

        // Header: MagicNumber (8 bytes) + Capacity (8 bytes) + NextAllocationOffset (8 bytes) = 24 bytes
        private const int HeaderSize = 24;
        // Index Entry: KeyHash (8 bytes) + DataOffset (8 bytes) + DataLength (4 bytes) + KeyLength (4 bytes) = 24 bytes
        private const int IndexEntrySize = 24;

        private static IDataProvider _dataProvider;
        private static readonly Lazy<MemoryMappedFileCacheProvider> _instance = new Lazy<MemoryMappedFileCacheProvider>(() => new MemoryMappedFileCacheProvider());
        
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly Mutex _globalWriteMutex;
        private readonly long _capacity;
        private readonly int _indexSize;
        private readonly int _dataAreaOffset;
        private long _disposed;
        private static long _totalRequests;
        private static long _cacheHits;
        private static long _cacheEntries;
        private static long _diskMisses;

        /// <summary>
        /// Gets the singleton instance of the cache provider.
        /// </summary>
        public static MemoryMappedFileCacheProvider Instance => _instance.Value;

        /// <summary>
        /// Property indicating the data is temporary in nature and should not be cached.
        /// </summary>
        public bool IsDataEphemeral { get; } = true;

        /// <summary>
        /// Initializes the data provider for the cache. This must be called before the instance is accessed.
        /// </summary>
        /// <param name="dataProvider">The data provider to use for fetching data on cache misses.</param>
        public static void Initialize(IDataProvider dataProvider)
        {
            _dataProvider = dataProvider;
        }

        /// <summary>
        /// Private constructor to enforce singleton pattern.
        /// </summary>
        private MemoryMappedFileCacheProvider()
        {
            if (_dataProvider == null)
            {
                throw new InvalidOperationException("MemoryMappedFileCacheProvider must be initialized with a data provider before use.");
            }

            _capacity = Config.GetInt("mmf-cache-capacity-gb", 1) * 1024L * 1024L * 1024L;
            _indexSize = Config.GetInt("mmf-cache-index-size", 100000);
            var indexAreaSize = _indexSize * IndexEntrySize;
            _dataAreaOffset = HeaderSize + indexAreaSize;

            var mutexName = $"Global\\{CacheName}_Mutex";
            _globalWriteMutex = new Mutex(initiallyOwned: false, name: mutexName);

            _mmf = MemoryMappedFile.CreateOrOpen(CacheName, _capacity, MemoryMappedFileAccess.ReadWrite);
            _accessor = _mmf.CreateViewAccessor();

            _globalWriteMutex.WaitOne();
            try
            {
                var magicNumber = _accessor.ReadInt64(0);
                if (magicNumber != MagicNumber)
                {
                    // Initialize the MMF header
                    _accessor.Write(0, MagicNumber);
                    _accessor.Write(8, _capacity);
                    _accessor.Write(16, (long)_dataAreaOffset);
                    // Zero out the index area
                    var zeroArray = new byte[indexAreaSize];
                    _accessor.WriteArray(HeaderSize, zeroArray, 0, zeroArray.Length);
                }
            }
            finally
            {
                _globalWriteMutex.ReleaseMutex();
            }

            Log.Trace($"MemoryMappedFileCacheProvider initialized with MMF: '{CacheName}'");
        }

        public Stream Fetch(string key)
        {
            if (Interlocked.Read(ref _disposed) == 1)
            {
                throw new ObjectDisposedException(nameof(MemoryMappedFileCacheProvider));
            }

            Interlocked.Increment(ref _totalRequests);
            var stream = TryFetchFromCache(key);
            if (stream != null)
            {
                Interlocked.Increment(ref _cacheHits);
                return stream;
            }

            // Slow path: cache miss, synchronize and fetch from data provider
            var mutexName = GetMutexName(key);
            using (var mutex = new Mutex(false, mutexName))
            {
                try
                {
                    mutex.WaitOne();

                    // Double-check cache after acquiring the lock
                    stream = TryFetchFromCache(key);
                    if (stream != null)
                    {
                        Interlocked.Increment(ref _cacheHits);
                        return stream;
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

        public void Store(string key, byte[] data)
        {
            if (Interlocked.Read(ref _disposed) == 1)
            {
                throw new ObjectDisposedException(nameof(MemoryMappedFileCacheProvider));
            }

            _globalWriteMutex.WaitOne();
            try
            {
                // Check if key already exists. If so, invalidate the old entry to perform an update.
                var existingIndexPos = FindIndexEntry(key, out _, out _, out _);
                if (existingIndexPos != -1)
                {
                    // Invalidate the old index entry by setting its hash to -1 (tombstone).
                    _accessor.Write(existingIndexPos, -1L);
                }
                else
                {
                    // This is a new entry
                    Interlocked.Increment(ref _cacheEntries);
                }

                var keyBytes = Encoding.UTF8.GetBytes(key);
                var requiredSize = keyBytes.Length + data.Length;
                var nextAllocationOffset = _accessor.ReadInt64(16);

                if (nextAllocationOffset + requiredSize > _capacity)
                {
                    Log.Error($"MemoryMappedFileCacheProvider.Store(): Not enough space to store key '{key}'. Required: {requiredSize}, Available: {_capacity - nextAllocationOffset}");
                    return;
                }

                // Write key and data to the data heap
                _accessor.WriteArray(nextAllocationOffset, keyBytes, 0, keyBytes.Length);
                _accessor.WriteArray(nextAllocationOffset + keyBytes.Length, data, 0, data.Length);

                // Create and write the index entry
                var keyHash = GetKeyHash(key);
                var indexPos = FindEmptyIndexSlot(keyHash);
                if (indexPos != -1)
                {
                    _accessor.Write(indexPos, keyHash);
                    _accessor.Write(indexPos + 8, nextAllocationOffset);
                    _accessor.Write(indexPos + 16, data.Length);
                    _accessor.Write(indexPos + 20, keyBytes.Length);

                    // Update the next allocation offset
                    _accessor.Write(16, nextAllocationOffset + requiredSize);
                }
                else
                {
                    Log.Error("MemoryMappedFileCacheProvider.Store(): Index is full. Could not store key '{key}'.");
                }
            }
            finally
            {
                _globalWriteMutex.ReleaseMutex();
            }
        }

        private Stream TryFetchFromCache(string key)
        {
            var indexPos = FindIndexEntry(key, out var dataOffset, out var dataLength, out _);
            if (indexPos != -1)
            {
                // We need to account for the key length stored before the data
                var keyLength = _accessor.ReadInt32(indexPos + 20);
                return new MemoryMappedViewSliceStream(_accessor, dataOffset + keyLength, dataLength);
            }
            return null;
        }

        private Stream FetchAndStore(string key)
        {
            LeanData.ParseKey(key, out var filename, out var entryName);

            byte[] dataToCache;

            if (filename.EndsWith(".zip", StringComparison.InvariantCultureIgnoreCase) && !string.IsNullOrEmpty(entryName))
            {
                // Handle zip file entries
                using (var stream = _dataProvider.Fetch(filename))
                {
                    if (stream == null)
                    {
                       Interlocked.Increment(ref _diskMisses);
                       return null;
                    }

                    try
                    {
                        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
                        {
                            var zipEntry = archive.Entries.FirstOrDefault(x => x.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));
                            if (zipEntry == null) return null;

                            using (var entryStream = zipEntry.Open())
                            using (var memoryStream = new MemoryStream())
                            {
                                entryStream.CopyTo(memoryStream);
                                dataToCache = memoryStream.ToArray();
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, $"MemoryMappedFileCacheProvider.FetchAndStore(): Corrupt zip file or entry: {filename}#{entryName}");
                        return null;
                    }
                }
            }
            else
            {
                // Handle non-zip files
                using (var stream = _dataProvider.Fetch(key))
                {
                    if (stream == null)
                    {
                       Interlocked.Increment(ref _diskMisses);
                       return null;
                    }
                    using (var memoryStream = new MemoryStream())
                    {
                        stream.CopyTo(memoryStream);
                        dataToCache = memoryStream.ToArray();
                    }
                }
            }

            if (dataToCache != null)
            {
                Store(key, dataToCache);
                // Now that it's stored, fetch it from the cache to get the MMF-backed stream,
                // ensuring consistent, zero-copy stream behavior for all cache hits and misses.
                return TryFetchFromCache(key);
            }

            return null;
        }

        private long FindIndexEntry(string key, out long dataOffset, out int dataLength, out int keyLength)
        {
            dataOffset = 0;
            dataLength = 0;
            keyLength = 0;
            var keyHash = GetKeyHash(key);
            var keyBytes = Encoding.UTF8.GetBytes(key);

            for (var i = 0; i < IndexSize; i++)
            {
                var index = ((uint)keyHash + i) % _indexSize;
                var indexPos = HeaderSize + index * IndexEntrySize;
                var storedHash = _accessor.ReadInt64(indexPos);

                if (storedHash == 0)
                {
                    // Truly empty slot, end of probe chain.
                    return -1;
                }

                if (storedHash == -1)
                {
                    // Tombstone, continue probing.
                    continue;
                }

                if (storedHash == keyHash)
                {
                    var storedKeyOffset = _accessor.ReadInt64(indexPos + 8);
                    var storedKeyLength = _accessor.ReadInt32(indexPos + 20);
                    if (storedKeyLength == keyBytes.Length)
                    {
                        var storedKeyBytes = new byte[storedKeyLength];
                        _accessor.ReadArray(storedKeyOffset, storedKeyBytes, 0, storedKeyLength);
                        if (keyBytes.SequenceEqual(storedKeyBytes))
                        {
                            dataOffset = _accessor.ReadInt64(indexPos + 8);
                            dataLength = _accessor.ReadInt32(indexPos + 16);
                            keyLength = storedKeyLength;
                            return indexPos;
                        }
                    }
                }
            }
            return -1;
        }

        private long FindEmptyIndexSlot(long keyHash)
        {
            for (var i = 0; i < IndexSize; i++)
            {
                var index = ((uint)keyHash + i) % _indexSize;
                var indexPos = HeaderSize + index * IndexEntrySize;
                var storedHash = _accessor.ReadInt64(indexPos);
                if (storedHash == 0 || storedHash == -1) // Can reuse empty or tombstoned slots
                {
                    return indexPos;
                }
            }
            return -1; // No empty slot found
        }

        private static long GetKeyHash(string key)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
                return BitConverter.ToInt64(hashBytes, 0);
            }
        }

        private static string GetMutexName(string key)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
                return $"Global\\LeanCache_{Convert.ToBase64String(hash).Replace("=", "").Replace("/", "_")}";
            }
        }

        /// <summary>
        /// Returns a list of zip entries in a provided zip file.
        /// This implementation does not use the cache for listing entries.
        /// </summary>
        public List<string> GetZipEntries(string zipFile)
        {
            using (var stream = _dataProvider.Fetch(zipFile))
            {
                if (stream == null)
                {
                    Interlocked.Increment(ref _diskMisses);
                    // Following ZipDataCacheProvider's pattern of throwing an exception when the file can't be found for GetZipEntries.
                    throw new ArgumentException($"Failed to get zip entries from {zipFile}, file not found.");
                }

                try
                {
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
                    {
                        return archive.Entries.Select(entry => entry.FullName).ToList();
                    }
                }
                catch (InvalidDataException e)
                {
                    Log.Error(e, $"MemoryMappedFileCacheProvider.GetZipEntries(): Corrupt zip file: {zipFile}");
                    throw new ArgumentException($"Failed to get zip entries from corrupt zip file {zipFile}", e);
                }
            }
        }

        /// <summary>
        /// Disposes of the cache provider, releasing the memory-mapped file and other resources.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1)
            {
                return;
            }

            var totalRequests = Interlocked.Read(ref _totalRequests);
            if (totalRequests > 0)
            {
                var cacheHits = Interlocked.Read(ref _cacheHits);
                var cacheEntries = Interlocked.Read(ref _cacheEntries);
                var diskMisses = Interlocked.Read(ref _diskMisses);
                var hitRate = (double)cacheHits / totalRequests;
                var usedBytes = _accessor.ReadInt64(16) - _dataAreaOffset;
                var usedMb = usedBytes / (1024.0 * 1024.0);

                Log.Trace($"MemoryMappedFileCacheProvider.Dispose(): Total Requests: {totalRequests}, Hit Rate: {hitRate:P}, " +
                          $"Cache Entries: {cacheEntries}, Disk Misses: {diskMisses}, Used Memory: {usedMb:F2} MB");
            }
            else
            {
                Log.Trace("MemoryMappedFileCacheProvider.Dispose(): No requests were made to the cache.");
            }

            _accessor?.Dispose();
            _mmf?.Dispose();
            _globalWriteMutex?.Dispose();
        }

        /// <summary>
        /// A custom stream that provides a seekable, read-only view over a slice of a memory-mapped file.
        /// </summary>
        private class MemoryMappedViewSliceStream : Stream
        {
            private readonly MemoryMappedViewAccessor _accessor;
            private readonly long _offset;
            private readonly long _length;
            private long _position;

            public MemoryMappedViewSliceStream(MemoryMappedViewAccessor accessor, long offset, long length)
            {
                _accessor = accessor;
                _offset = offset;
                _length = length;
                _position = 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _position;
                set => Seek(value, SeekOrigin.Begin);
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
                if (buffer.Length - offset < count) throw new ArgumentException("Invalid offset and length.");

                var remaining = _length - _position;
                if (remaining <= 0) return 0; // End of stream

                var bytesToRead = (int)Math.Min(count, remaining);
                
                _accessor.ReadArray(_offset + _position, buffer, offset, bytesToRead);

                _position += bytesToRead;
                return bytesToRead;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                long newPosition;
                switch (origin)
                {
                    case SeekOrigin.Begin:
                        newPosition = offset;
                        break;
                    case SeekOrigin.Current:
                        newPosition = _position + offset;
                        break;
                    case SeekOrigin.End:
                        newPosition = _length + offset;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(origin));
                }

                if (newPosition < 0 || newPosition > _length)
                {
                    throw new IOException("Seek out of bounds.");
                }

                _position = newPosition;
                return _position;
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                // No-op, as the underlying MMF is managed by the provider.
                base.Dispose(disposing);
            }
        }
    }
}