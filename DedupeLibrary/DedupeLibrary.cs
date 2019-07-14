﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SlidingWindow;

namespace WatsonDedupe
{
    /// <summary>
    /// Library for deduplication of objects.
    /// </summary>
    public class DedupeLibrary
    {
        #region Public-Members

        /// <summary>
        /// Enable or disable console logging for deduplication operations.
        /// </summary>
        public bool DebugDedupe;

        /// <summary>
        /// Enable or disable console logging for SQL operations.
        /// </summary>
        public bool DebugSql;

        /// <summary>
        /// Callback methods used by the dedupe library to read, write, and delete chunks.
        /// </summary>
        public CallbackMethods Callbacks = new CallbackMethods();

        #endregion

        #region Private-Members

        private string _IndexFile;
        private int _MinChunkSize;
        private int _MaxChunkSize;
        private int _ShiftCount;
        private int _BoundaryCheckBytes;
        private readonly object _ChunkLock;

        private SqliteWrapper _Sqlite;
         
        #endregion

        #region Constructor

        /// <summary>
        /// Initialize from an existing index.
        /// </summary>
        /// <param name="indexFile">Path and filename.</param>
        /// <param name="writeChunkMethod">Method to call to write a chunk to storage.</param>
        /// <param name="readChunkMethod">Method to call to read a chunk from storage.</param>
        /// <param name="deleteChunkMethod">Method to call to delete a chunk from storage.</param>
        /// <param name="debugDedupe">Enable console logging for deduplication operations.</param>
        /// <param name="debugSql">Enable console logging for SQL operations.</param>
        public DedupeLibrary(string indexFile, Func<Chunk, bool> writeChunkMethod, Func<string, byte[]> readChunkMethod, Func<string, bool> deleteChunkMethod, bool debugDedupe, bool debugSql)
        {
            if (String.IsNullOrEmpty(indexFile)) throw new ArgumentNullException(nameof(indexFile));
            if (!File.Exists(indexFile)) throw new FileNotFoundException("Index file not found.");
            if (writeChunkMethod == null) throw new ArgumentNullException(nameof(writeChunkMethod));
            if (readChunkMethod == null) throw new ArgumentNullException(nameof(readChunkMethod));
            if (deleteChunkMethod == null) throw new ArgumentNullException(nameof(deleteChunkMethod));

            _IndexFile = DedupeCommon.SanitizeString(indexFile);

            Callbacks = new CallbackMethods();
            Callbacks.WriteChunk = writeChunkMethod;
            Callbacks.ReadChunk = readChunkMethod;
            Callbacks.DeleteChunk = deleteChunkMethod;

            DebugDedupe = debugDedupe;
            DebugSql = debugSql;
            _ChunkLock = new object();

            _Sqlite = new SqliteWrapper(_IndexFile, DebugSql);

            InitFromExistingIndex();
        }

        /// <summary>
        /// Create a new index.
        /// </summary>
        /// <param name="indexFile">Path and filename.</param>
        /// <param name="minChunkSize">Minimum chunk size, must be divisible by 8, divisible by 64, and 128 or greater.</param>
        /// <param name="maxChunkSize">Maximum chunk size, must be divisible by 8, divisible by 64, and at least 8 times larger than minimum chunk size.</param>
        /// <param name="shiftCount">Number of bytes to shift while identifying chunk boundaries, must be less than or equal to minimum chunk size.</param>
        /// <param name="boundaryCheckBytes">Number of bytes to examine while checking for a chunk boundary, must be 8 or fewer.</param>
        /// <param name="writeChunkMethod">Method to call to write a chunk to storage.</param>
        /// <param name="readChunkMethod">Method to call to read a chunk from storage.</param>
        /// <param name="deleteChunkMethod">Method to call to delete a chunk from storage.</param>
        /// <param name="debugDedupe">Enable console logging for deduplication operations.</param>
        /// <param name="debugSql">Enable console logging for SQL operations.</param>
        public DedupeLibrary(
            string indexFile,
            int minChunkSize,
            int maxChunkSize,
            int shiftCount, 
            int boundaryCheckBytes,
            Func<Chunk, bool> writeChunkMethod, 
            Func<string, byte[]> readChunkMethod,
            Func<string, bool> deleteChunkMethod,
            bool debugDedupe,
            bool debugSql)
        {
            if (String.IsNullOrEmpty(indexFile)) throw new ArgumentNullException(nameof(indexFile));
            if (minChunkSize % 8 != 0) throw new ArgumentException("Value for minChunkSize must be evenly divisible by 8.");
            if (maxChunkSize % 8 != 0) throw new ArgumentException("Value for maxChunkSize must be evenly divisible by 8.");
            if (minChunkSize % 64 != 0) throw new ArgumentException("Value for minChunkSize must be evenly divisible by 64.");
            if (maxChunkSize % 64 != 0) throw new ArgumentException("Value for maxChunkSize must be evenly divisible by 64.");
            if (minChunkSize < 128) throw new ArgumentOutOfRangeException("Value for minChunkSize must be 128 or greater.");
            if (maxChunkSize <= minChunkSize) throw new ArgumentOutOfRangeException("Value for maxChunkSize must be greater than minChunkSize and " + (8 * minChunkSize) + " or less.");
            if (maxChunkSize < (8 * minChunkSize)) throw new ArgumentOutOfRangeException("Value for maxChunkSize must be " + (8 * minChunkSize) + " or greater.");
            if (shiftCount > minChunkSize) throw new ArgumentOutOfRangeException("Value for shiftCount must be less than or equal to minChunkSize.");
            if (writeChunkMethod == null) throw new ArgumentNullException(nameof(writeChunkMethod));
            if (readChunkMethod == null) throw new ArgumentNullException(nameof(readChunkMethod));
            if (deleteChunkMethod == null) throw new ArgumentNullException(nameof(deleteChunkMethod));
            if (boundaryCheckBytes < 1 || boundaryCheckBytes > 8) throw new ArgumentNullException(nameof(boundaryCheckBytes));

            if (File.Exists(indexFile)) throw new IOException("Index file already exists.");

            _IndexFile = DedupeCommon.SanitizeString(indexFile);
            _MinChunkSize = minChunkSize;
            _MaxChunkSize = maxChunkSize;
            _ShiftCount = shiftCount;
            _BoundaryCheckBytes = boundaryCheckBytes;

            Callbacks = new CallbackMethods();
            Callbacks.WriteChunk = writeChunkMethod;
            Callbacks.ReadChunk = readChunkMethod;
            Callbacks.DeleteChunk = deleteChunkMethod; 

            DebugDedupe = debugDedupe;
            DebugSql = debugSql;
            _ChunkLock = new object();

            _Sqlite = new SqliteWrapper(_IndexFile, DebugSql);

            InitNewIndex();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Store an object in the deduplication index.
        /// </summary>
        /// <param name="objectName">The name of the object.  Must be unique in the index.</param>
        /// <param name="data">The byte data for the object.</param>
        /// <param name="chunks">The list of chunks identified during the deduplication operation.</param>
        /// <returns>True if successful.</returns>
        public bool StoreObject(string objectName, byte[] data, out List<Chunk> chunks)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
            return StoreObject(objectName, Callbacks, data.Length, DedupeCommon.BytesToStream(data), out chunks);
        }

        /// <summary>
        /// Store an object in the deduplication index.
        /// </summary>
        /// <param name="objectName">The name of the object.  Must be unique in the index.</param>
        /// <param name="contentLength">The length of the data.</param>
        /// <param name="stream">The stream containing the data.</param>
        /// <param name="chunks">The list of chunks identified during the deduplication operation.</param>
        /// <returns>True if successful.</returns>
        public bool StoreObject(string objectName, long contentLength, Stream stream, out List<Chunk> chunks)
        {
            return StoreObject(objectName, Callbacks, contentLength, stream, out chunks);
        }

        /// <summary>
        /// Store an object in the deduplication index.
        /// This method will use the callbacks supplied in the method signature.
        /// </summary>
        /// <param name="objectName">The name of the object.  Must be unique in the index.</param>
        /// <param name="callbacks">CallbackMethods object containing callback methods.</param>
        /// <param name="data">The byte data for the object.</param>
        /// <param name="chunks">The list of chunks identified during the deduplication operation.</param>
        /// <returns>True if successful.</returns>
        public bool StoreObject(string objectName, CallbackMethods callbacks, byte[] data, out List<Chunk> chunks)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
            return StoreObject(objectName, callbacks, data.Length, DedupeCommon.BytesToStream(data), out chunks);
        }

        /// <summary>
        /// Store an object in the deduplication index.
        /// This method will use the callbacks supplied in the method signature.
        /// </summary>
        /// <param name="objectName">The name of the object.  Must be unique in the index.</param>
        /// <param name="callbacks">CallbackMethods object containing callback methods.</param>
        /// <param name="contentLength">The length of the data.</param>
        /// <param name="stream">The stream containing the data.</param>
        /// <param name="chunks">The list of chunks identified during the deduplication operation.</param>
        /// <returns>True if successful.</returns>
        public bool StoreObject(string objectName, CallbackMethods callbacks, long contentLength, Stream stream, out List<Chunk> chunks)
        {
            #region Initialize

            chunks = new List<Chunk>();
            if (String.IsNullOrEmpty(objectName)) throw new ArgumentNullException(nameof(objectName));
            if (callbacks == null) throw new ArgumentNullException(nameof(callbacks));
            if (callbacks.WriteChunk == null) throw new ArgumentException("WriteChunk callback must be specified.");
            if (callbacks.DeleteChunk == null) throw new ArgumentException("DeleteChunk callback must be specified.");
            if (contentLength < 1) throw new ArgumentException("Content length must be at least one byte.");
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("Cannot read from supplied stream.");
            objectName = DedupeCommon.SanitizeString(objectName);

            if (_Sqlite.ObjectExists(objectName))
            {
                Log("Object " + objectName + " already exists");
                return false;
            }

            bool garbageCollectionRequired = false;

            #endregion

            #region Chunk-Data

            try
            {
                Func<Chunk, bool> processChunk = delegate (Chunk chunk)
                {
                    if (chunk == null) return false;

                    lock (_ChunkLock)
                    {
                        if (!_Sqlite.AddObjectChunk(objectName, contentLength, chunk))
                        {
                            Log("Unable to add chunk key " + chunk.Key);
                            garbageCollectionRequired = true;
                            return false;
                        } 

                        if (!callbacks.WriteChunk(chunk))
                        {
                            Log("Unable to write chunk key " + chunk.Key);
                            garbageCollectionRequired = true;
                            return false;
                        }
                    }

                    return true;
                };

                if (!ChunkStream(contentLength, stream, processChunk, out chunks))
                {
                    Log("Unable to chunk object " + objectName);
                    garbageCollectionRequired = true;
                    return false;
                }
            }
            finally
            {
                if (garbageCollectionRequired)
                {
                    List<string> garbageCollectKeys = new List<string>();
                    _Sqlite.DeleteObjectChunks(objectName, out garbageCollectKeys);

                    if (garbageCollectKeys != null && garbageCollectKeys.Count > 0)
                    {
                        foreach (string key in garbageCollectKeys)
                        {
                            if (!callbacks.DeleteChunk(key))
                            {
                                Log("Unable to garbage collect chunk " + key);
                            }
                        }
                    }
                }
            }

            #endregion
             
            return true; 
        }

        /// <summary>
        /// Store an object within a container in the deduplication index if it doesn't already exist, or, replace the object if it does.
        /// </summary>
        /// <param name="objectName">The name of the object.  Must be unique in the index.</param>
        /// <param name="data">The byte data for the object.</param>
        /// <param name="chunks">The list of chunks identified during the deduplication operation.</param>
        /// <returns>True if successful.</returns>
        public bool StoreOrReplaceObject(string objectName, byte[] data, out List<Chunk> chunks)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
            return StoreOrReplaceObject(objectName, Callbacks, data.Length, DedupeCommon.BytesToStream(data), out chunks);
        }

        /// <summary>
        /// Store an object within a container in the deduplication index if it doesn't already exist, or, replace the object if it does.
        /// </summary>
        /// <param name="objectName">The name of the object.  Must be unique in the index.</param>
        /// <param name="contentLength">The length of the data.</param>
        /// <param name="stream">The stream containing the data.</param>
        /// <param name="chunks">The list of chunks identified during the deduplication operation.</param>
        /// <returns>True if successful.</returns>
        public bool StoreOrReplaceObject(string objectName, long contentLength, Stream stream, out List<Chunk> chunks)
        {
            return StoreOrReplaceObject(objectName, Callbacks, contentLength, stream, out chunks);
        }

        /// <summary>
        /// Store an object within a container in the deduplication index if it doesn't already exist, or, replace the object if it does.
        /// This method will use the callbacks supplied in the method signature.
        /// </summary>
        /// <param name="objectName">The name of the object.  Must be unique in the index.</param>
        /// <param name="callbacks">CallbackMethods object containing callback methods.</param>
        /// <param name="data">The byte data for the object.</param>
        /// <param name="chunks">The list of chunks identified during the deduplication operation.</param>
        /// <returns>True if successful.</returns>
        public bool StoreOrReplaceObject(string objectName, CallbackMethods callbacks, byte[] data, out List<Chunk> chunks)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
            return StoreOrReplaceObject(objectName, callbacks, data.Length, DedupeCommon.BytesToStream(data), out chunks); 
        }

        /// <summary>
        /// Store an object within a container in the deduplication index if it doesn't already exist, or, replace the object if it does.
        /// This method will use the callbacks supplied in the method signature.
        /// </summary>
        /// <param name="objectName">The name of the object.  Must be unique in the index.</param>
        /// <param name="callbacks">CallbackMethods object containing callback methods.</param>
        /// <param name="contentLength">The length of the data.</param>
        /// <param name="stream">The stream containing the data.</param>
        /// <param name="chunks">The list of chunks identified during the deduplication operation.</param>
        /// <returns>True if successful.</returns>
        public bool StoreOrReplaceObject(string objectName, CallbackMethods callbacks, long contentLength, Stream stream, out List<Chunk> chunks)
        {
            #region Initialize

            chunks = new List<Chunk>();
            if (String.IsNullOrEmpty(objectName)) throw new ArgumentNullException(nameof(objectName));
            if (callbacks == null) throw new ArgumentNullException(nameof(callbacks));
            if (callbacks.WriteChunk == null) throw new ArgumentException("WriteChunk callback must be specified.");
            if (callbacks.DeleteChunk == null) throw new ArgumentException("DeleteChunk callback must be specified.");
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("Cannot read from supplied stream.");
            objectName = DedupeCommon.SanitizeString(objectName);

            #endregion

            #region Delete-if-Exists

            if (_Sqlite.ObjectExists(objectName))
            {
                Log("Object " + objectName + " already exists, deleting");
                if (!DeleteObject(objectName))
                {
                    Log("Unable to delete existing object");
                    return false;
                }
                else
                {
                    Log("Successfully deleted object for replacement");
                }
            }

            #endregion

            return StoreObject(objectName, callbacks, contentLength, stream, out chunks); 
        }

        /// <summary>
        /// Retrieve metadata about an object from the deduplication index.
        /// </summary>
        /// <param name="objectName">The name of the object.</param>
        /// <param name="md">Object metadata.</param>
        /// <returns>True if successful.</returns>
        public bool RetrieveObjectMetadata(string objectName, out ObjectMetadata md)
        {
            md = null;
            if (String.IsNullOrEmpty(objectName)) throw new ArgumentNullException(nameof(objectName));
            objectName = DedupeCommon.SanitizeString(objectName);

            lock (_ChunkLock)
            {
                return _Sqlite.GetObjectMetadata(objectName, out md);
            }
        }

        /// <summary>
        /// Retrieve an object from the deduplication index.
        /// </summary>
        /// <param name="objectName">The name of the object.</param>
        /// <param name="data">The byte data from the object.</param>
        /// <returns>True if successful.</returns>
        public bool RetrieveObject(string objectName, out byte[] data)
        {
            long contentLength = 0;
            Stream stream = null;
            bool success = RetrieveObject(objectName, Callbacks, out contentLength, out stream);
            data = DedupeCommon.StreamToBytes(stream);
            return success; 
        }

        /// <summary>
        /// Retrieve an object from the deduplication index.
        /// </summary>
        /// <param name="objectName">The name of the object.</param>
        /// <param name="contentLength">The length of the data.</param>
        /// <param name="stream">The stream containing the data.</param>
        /// <returns>True if successful.</returns>
        public bool RetrieveObject(string objectName, out long contentLength, out Stream stream)
        {
            return RetrieveObject(objectName, Callbacks, out contentLength, out stream);
        }

        /// <summary>
        /// Retrieve an object from the deduplication index.
        /// This method will use the callbacks supplied in the method signature.
        /// </summary>
        /// <param name="objectName">The name of the object.</param>
        /// <param name="callbacks">CallbackMethods object containing callback methods.</param>
        /// <param name="data">The byte data from the object.</param>
        /// <returns>True if successful.</returns>
        public bool RetrieveObject(string objectName, CallbackMethods callbacks, out byte[] data)
        {
            long contentLength = 0;
            Stream stream = null;
            bool success = RetrieveObject(objectName, callbacks, out contentLength, out stream);
            data = DedupeCommon.StreamToBytes(stream);
            return success; 
        }

        /// <summary>
        /// Retrieve an object from the deduplication index.
        /// This method will use the callbacks supplied in the method signature.
        /// </summary>
        /// <param name="objectName">The name of the object.</param>
        /// <param name="callbacks">CallbackMethods object containing callback methods.</param>
        /// <param name="contentLength">The length of the data.</param>
        /// <param name="stream">The stream containing the data.</param>
        /// <returns>True if successful.</returns>
        public bool RetrieveObject(string objectName, CallbackMethods callbacks, out long contentLength, out Stream stream)
        {
            stream = null;
            contentLength = 0;
            if (String.IsNullOrEmpty(objectName)) throw new ArgumentNullException(nameof(objectName));
            if (callbacks == null) throw new ArgumentNullException(nameof(callbacks));
            if (callbacks.ReadChunk == null) throw new ArgumentException("ReadChunk callback must be specified.");
            objectName = DedupeCommon.SanitizeString(objectName);

            ObjectMetadata md = null;

            lock (_ChunkLock)
            {
                if (!_Sqlite.GetObjectMetadata(objectName, out md))
                {
                    Log("Unable to retrieve object metadata for object " + objectName);
                    return false;
                }

                if (md.Chunks == null || md.Chunks.Count < 1)
                {
                    Log("No chunks returned");
                    return false;
                }

                stream = new MemoryStream();
                 
                foreach (Chunk curr in md.Chunks)
                {
                    byte[] chunkData = callbacks.ReadChunk(curr.Key);
                    if (chunkData == null || chunkData.Length < 1)
                    {
                        Log("Unable to read chunk " + curr.Key);
                        return false;
                    }

                    stream.Write(chunkData, 0, chunkData.Length);
                    contentLength += chunkData.Length;
                }

                if (contentLength > 0) stream.Seek(0, SeekOrigin.Begin);
            }

            return true;
        }

        /// <summary>
        /// Delete an object stored in the deduplication index.
        /// </summary>
        /// <param name="objectName">The name of the object.</param>
        /// <returns>True if successful.</returns>
        public bool DeleteObject(string objectName)
        {
            return DeleteObject(objectName, Callbacks);
        }

        /// <summary>
        /// Delete an object stored in the deduplication index.
        /// This method will use the callbacks supplied in the method signature.
        /// </summary>
        /// <param name="objectName">The name of the object.</param>
        /// <param name="callbacks">CallbackMethods object containing callback methods.</param>
        /// <returns>True if successful.</returns>
        public bool DeleteObject(string objectName, CallbackMethods callbacks)
        {
            if (String.IsNullOrEmpty(objectName)) throw new ArgumentNullException(nameof(objectName));
            if (callbacks == null) throw new ArgumentNullException(nameof(callbacks));
            if (callbacks.DeleteChunk == null) throw new ArgumentException("DeleteChunk callback must be specified.");
            objectName = DedupeCommon.SanitizeString(objectName);

            List<string> garbageCollectChunks = null;

            lock (_ChunkLock)
            {
                _Sqlite.DeleteObjectChunks(objectName, out garbageCollectChunks);
                if (garbageCollectChunks != null && garbageCollectChunks.Count > 0)
                {
                    foreach (string key in garbageCollectChunks)
                    {
                        if (!callbacks.DeleteChunk(key))
                        {
                            Log("Unable to delete chunk: " + key);
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// List the objects stored in the deduplication index.
        /// </summary>
        /// <param name="keys">List of object names.</param>
        public void ListObjects(out List<string> keys)
        {
            _Sqlite.ListObjects(out keys);
            return;
        }

        /// <summary>
        /// Determine if an object exists in the index.
        /// </summary>
        /// <param name="objectName">The name of the object.</param>
        /// <returns>Boolean indicating if the object exists.</returns>
        public bool ObjectExists(string objectName)
        {
            return _Sqlite.ObjectExists(objectName);
        }

        /// <summary>
        /// Determine if a chunk exists in the index.
        /// </summary>
        /// <param name="chunkKey">The chunk's key.</param>
        /// <returns>Boolean indicating if the chunk exists.</returns>
        public bool ChunkExists(string chunkKey)
        {
            return _Sqlite.ChunkExists(chunkKey);
        }

        /// <summary>
        /// Retrieve deduplication index statistics.
        /// </summary>
        /// <param name="numObjects">The number of objects stored in the index.</param>
        /// <param name="numChunks">Number of chunks referenced in the index.</param>
        /// <param name="logicalBytes">The amount of data stored in the index, i.e. the full size of the original data.</param>
        /// <param name="physicalBytes">The number of bytes consumed by chunks of data, i.e. the deduplication set size.</param>
        /// <param name="dedupeRatioX">Deduplication ratio represented as a multiplier.</param>
        /// <param name="dedupeRatioPercent">Deduplication ratio represented as a percentage.</param>
        /// <returns>True if successful.</returns>
        public bool IndexStats(out int numObjects, out int numChunks, out long logicalBytes, out long physicalBytes, out decimal dedupeRatioX, out decimal dedupeRatioPercent)
        {
            return _Sqlite.IndexStats(out numObjects, out numChunks, out logicalBytes, out physicalBytes, out dedupeRatioX, out dedupeRatioPercent);
        }

        /// <summary>
        /// Copies the index database to another file.
        /// </summary>
        /// <param name="destination">The destination file.</param>
        /// <returns>True if successful.</returns>
        public bool BackupIndex(string destination)
        {
            return _Sqlite.BackupIndex(destination);
        }

        #endregion

        #region Private-Methods

        private void InitFromExistingIndex()
        {
            string tempVal;
            if (!_Sqlite.GetConfigData("min_chunk_size", out tempVal))
            {
                throw new Exception("Configuration table has invalid value for 'min_chunk_size'.");
            }
            else
            {
                Log("MinChunkSize set to " + tempVal);
                _MinChunkSize = Convert.ToInt32(tempVal);
            }

            if (!_Sqlite.GetConfigData("max_chunk_size", out tempVal))
            {
                throw new Exception("Configuration table has invalid value for 'max_chunk_size'.");
            }
            else
            {
                Log("MaxChunkSize set to " + tempVal);
                _MaxChunkSize = Convert.ToInt32(tempVal);
            }

            if (!_Sqlite.GetConfigData("shift_count", out tempVal))
            {
                throw new Exception("Configuration table has invalid value for 'shift_count'.");
            }
            else
            {
                Log("ShiftCount set to " + tempVal);
                _ShiftCount = Convert.ToInt32(tempVal);
            }

            if (!_Sqlite.GetConfigData("boundary_check_bytes", out tempVal))
            {
                throw new Exception("Configuration table has invalid value for 'boundary_check_bytes'.");
            }
            else
            {
                Log("BoundaryCheckBytes set to " + tempVal);
                _BoundaryCheckBytes = Convert.ToInt32(tempVal);
            }
        }

        private void InitNewIndex()
        {
            _Sqlite.AddConfigData("min_chunk_size", _MinChunkSize.ToString());
            _Sqlite.AddConfigData("max_chunk_size", _MaxChunkSize.ToString());
            _Sqlite.AddConfigData("shift_count", _ShiftCount.ToString());
            _Sqlite.AddConfigData("boundary_check_bytes", _BoundaryCheckBytes.ToString());
            _Sqlite.AddConfigData("index_per_object", "false");
        }

        private bool ChunkObject(byte[] data, out List<Chunk> chunks)
        {
            #region Initialize

            chunks = new List<Chunk>();
            Chunk c;

            if (data == null || data.Length < 1) return false;

            if (data.Length <= _MinChunkSize)
            {
                c = new Chunk(
                    DedupeCommon.BytesToBase64(DedupeCommon.Sha256(data)),
                    data.Length,
                    0,
                    0,
                    data);
                chunks.Add(c);
                return true;
            }

            int currPosition = 0;
            int chunkStart = 0;
            byte[] window = new byte[_MinChunkSize];

            #endregion

            #region Setup-First-Window

            Buffer.BlockCopy(data, 0, window, 0, _MinChunkSize);
            currPosition = _MinChunkSize;

            #endregion

            #region Process

            int chunksFound = 0;
            int bytesTotal = 0;
            
            Log("Chunking " + data.Length + " bytes of data");
            while (currPosition < data.Length)
            {
                byte[] md5Hash = DedupeCommon.Md5(window);
                if (DebugDedupe)
                {
                    if (currPosition % 1000 == 0) Console.Write("Chunk start " + chunkStart + " window end " + currPosition + " hash: " + DedupeCommon.BytesToBase64(md5Hash) + "\r");
                }

                if (DedupeCommon.IsZeroBytes(md5Hash, _BoundaryCheckBytes))
                {
                    #region New-Chunk-Identified

                    if (DebugDedupe)
                    {
                        DedupeCommon.ClearCurrentLine();
                        Console.Write
                            ("\rChunk identified from " + chunkStart + " to " + currPosition + " (" + (currPosition - chunkStart) + " bytes)");
                    }

                    // create chunk
                    byte[] chunk = new byte[(currPosition - chunkStart)];
                    Buffer.BlockCopy(data, chunkStart, chunk, 0, (currPosition - chunkStart));

                    // add to chunk list
                    c = new Chunk(
                        DedupeCommon.BytesToBase64(DedupeCommon.Sha256(chunk)),
                        chunk.Length,
                        chunksFound,
                        chunkStart,
                        chunk);
                    chunks.Add(c);
                    chunksFound++;
                    bytesTotal += (currPosition - chunkStart);

                    chunkStart = currPosition;

                    // initialize new window
                    if (data.Length - currPosition >= _MinChunkSize)
                    {
                        #region Min-Size-or-More-Remaining

                        window = new byte[_MinChunkSize];
                        Buffer.BlockCopy(data, currPosition, window, 0, _MinChunkSize);
                        currPosition += _MinChunkSize;
                        continue;

                        #endregion
                    }
                    else
                    {
                        #region Less-Than-Min-Size-Remaining

                        // end of data
                        if (DebugDedupe)
                        {
                            DedupeCommon.ClearCurrentLine();
                            Console.WriteLine("Less than MinChunkSize remaining, adding chunk (" + (data.Length - currPosition) + " bytes)");
                        }

                        chunk = new byte[(data.Length - currPosition)];
                        Buffer.BlockCopy(data, currPosition, chunk, 0, (data.Length - currPosition));

                        // add to chunk list
                        c = new Chunk(
                            DedupeCommon.BytesToBase64(DedupeCommon.Sha256(chunk)),
                            chunk.Length,
                            chunksFound,
                            currPosition,
                            chunk);
                        chunks.Add(c);
                        chunksFound++;
                        bytesTotal += (data.Length - currPosition);

                        window = null;

                        // end processing
                        break;

                        #endregion
                    }

                    #endregion
                }
                else
                {
                    #region Not-a-Chunk-Boundary

                    if ((currPosition - chunkStart) >= _MaxChunkSize)
                    {
                        #region Max-Size-Reached

                        // create chunk
                        byte[] chunk = new byte[(currPosition - chunkStart)];
                        Buffer.BlockCopy(data, chunkStart, chunk, 0, (currPosition - chunkStart));
                        // if (Debug) Console.WriteLine("chunk identified due to max size from " + chunk_start + " to " + curr_pos + " (" + (curr_pos - chunk_start) + " bytes)");

                        // add to chunk list
                        c = new Chunk(
                            DedupeCommon.BytesToBase64(DedupeCommon.Sha256(chunk)),
                            chunk.Length,
                            chunksFound,
                            chunkStart,
                            chunk);
                        chunks.Add(c);
                        chunksFound++;
                        bytesTotal += (currPosition - chunkStart);

                        chunkStart = currPosition;

                        // initialize new window
                        if (data.Length - currPosition >= _MinChunkSize)
                        {
                            #region Min-Size-or-More-Remaining

                            window = new byte[_MinChunkSize];
                            Buffer.BlockCopy(data, currPosition, window, 0, _MinChunkSize);
                            currPosition += _MinChunkSize;
                            continue;

                            #endregion
                        }
                        else
                        {
                            #region Less-Than-Min-Size-Remaining

                            // end of data
                            Log("Less than MinChunkSize remaining, adding chunk (" + (data.Length - currPosition) + " bytes)");
                            chunk = new byte[(data.Length - currPosition)];
                            Buffer.BlockCopy(data, currPosition, chunk, 0, (data.Length - currPosition));

                            // add to chunk list
                            c = new Chunk(
                                DedupeCommon.BytesToBase64(DedupeCommon.Sha256(chunk)),
                                chunk.Length,
                                chunksFound,
                                currPosition,
                                chunk);
                            chunks.Add(c);
                            chunksFound++;
                            bytesTotal += (data.Length - currPosition);

                            window = null;

                            // end processing
                            break;

                            #endregion
                        }

                        #endregion
                    }
                    else
                    {
                        #region Shift-Window

                        // shift the window
                        window = DedupeCommon.ShiftLeft(window, _ShiftCount, 0x00);

                        // add the next set of bytes to the window
                        if (currPosition + _ShiftCount > data.Length)
                        {
                            //
                            // set current position to the end and break
                            //
                            currPosition = data.Length;
                            break;
                        }
                        else
                        {
                            Buffer.BlockCopy(data, currPosition, window, (_MinChunkSize - _ShiftCount), _ShiftCount);
                        }

                        // increment the current position
                        currPosition = currPosition + _ShiftCount;

                        #endregion
                    }

                    #endregion
                }
            }

            if (window != null)
            {
                #region Last-Chunk

                if (DebugDedupe)
                {
                    DedupeCommon.ClearCurrentLine();
                    Console.WriteLine("\rChunk identified (end of input) from " + chunkStart + " to " + currPosition + " (" + (currPosition - chunkStart) + " bytes)");
                }

                // if (Debug) Console.WriteLine("adding leftover chunk (" + (data.Length - chunk_start) + " bytes)");
                byte[] chunk = new byte[(data.Length - chunkStart)];
                Buffer.BlockCopy(data, chunkStart, chunk, 0, (data.Length - chunkStart));

                // add to chunk list
                c = new Chunk(
                    DedupeCommon.BytesToBase64(DedupeCommon.Sha256(chunk)),
                    chunk.Length,
                    chunksFound,
                    chunkStart,
                    chunk);
                chunks.Add(c);
                chunksFound++;
                bytesTotal += (currPosition - chunkStart);

                #endregion
            }

            #endregion

            #region Respond

            if (DebugDedupe)
            {
                DedupeCommon.ClearCurrentLine();
                Console.WriteLine("Returning " + chunks.Count + " chunks (" + bytesTotal + " bytes)");
            }

            return true;

            #endregion
        }

        private bool ChunkStream(long contentLength, Stream stream, Func<Chunk, bool> processChunk, out List<Chunk> chunks)
        {
            #region Initialize

            chunks = new List<Chunk>();
            Chunk chunk = null;
            long bytesRead = 0;
            string key = null;

            if (stream == null || !stream.CanRead || contentLength < 1) return false;

            #endregion

            #region Single-Chunk

            if (contentLength <= _MinChunkSize)
            {
                byte[] chunkData = DedupeCommon.ReadBytesFromStream(stream, contentLength, out bytesRead);
                key = DedupeCommon.BytesToBase64(DedupeCommon.Sha256(chunkData));
                chunk = new Chunk(
                    key,
                    contentLength,
                    0,
                    0,
                    chunkData);
                chunks.Add(chunk);
                return processChunk(chunk);
            }

            #endregion

            #region Process-Sliding-Window

            Streams streamWindow = new Streams(stream, contentLength, _MinChunkSize, _ShiftCount);
            byte[] currChunk = null;   
            long chunkPosition = 0;     // should only be set at the beginning of a new chunk

            while (true)
            {
                byte[] newData = null;
                bool finalChunk = false;

                long tempPosition = 0;
                byte[] window = streamWindow.GetNextChunk(out tempPosition, out newData, out finalChunk);
                if (window == null) return true;
                if (currChunk == null) chunkPosition = tempPosition;
                
                if (currChunk == null)
                    currChunk = window; // starting a new chunk
                else
                    currChunk = DedupeCommon.AppendBytes(currChunk, newData); // append new data

                byte[] md5Hash = DedupeCommon.Md5(window);
                if (DedupeCommon.IsZeroBytes(md5Hash, _BoundaryCheckBytes)
                    ||
                    (currChunk.Length >= _MaxChunkSize))
                {
                    #region Chunk-Boundary
                     
                    key = DedupeCommon.BytesToBase64(DedupeCommon.Sha256(currChunk));
                    chunk = new Chunk(
                        key,
                        currChunk.Length,
                        chunks.Count,
                        chunkPosition,
                        currChunk);
                    chunks.Add(chunk);
                     
                    if (!processChunk(chunk)) return false;

                    chunk = null;
                    currChunk = null;

                    #endregion
                }
                else
                { 
                    // do nothing, continue; 
                }

                if (finalChunk)
                {
                    #region Final-Chunk

                    if (chunk != null && currChunk != null)
                    {
                        key = DedupeCommon.BytesToBase64(DedupeCommon.Sha256(currChunk));
                        chunk = new Chunk(
                            key,
                            currChunk.Length,
                            chunks.Count,
                            chunkPosition,
                            currChunk);
                        chunks.Add(chunk);
                        
                        if (!processChunk(chunk)) return false;

                        chunk = null;
                        currChunk = null;
                        break;
                    }

                    #endregion
                }
            }

            #endregion

            return true; 
        }

        private void Log(string msg)
        {
            if (DebugDedupe) Console.WriteLine(msg);
        }

        #endregion
    }
}
