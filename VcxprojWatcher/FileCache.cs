using LiteDB;
using Murmur;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CodeWeave
{
    // Define the data model for the cache table
    public class CachedFile
    {
        [BsonId] // Marks this property as the primary key
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public string LastModified { get; set; }
        public string FileHash { get; set; }
        public string CachedAt { get; set; }
    }

    public class FileHashCache : IDisposable
    {
        private readonly LiteDatabase db_;
        private readonly ILiteCollection<CachedFile> collection_;

        public FileHashCache(string dbPath = "file_cache.db")
        {
            // Open database (or create it if it doesn't exist)
            db_ = new LiteDatabase(dbPath);
            
            // Get collection for the model
            collection_ = db_.GetCollection<CachedFile>("file_cache");

            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            // Create a compound-like unique index to optimize lookup
            // LiteDB supports expressions for defining indexes
            collection_.EnsureIndex("LookupIdx", "$.FilePath + '_' + STRING($.FileSize) + '_' + $.LastModified", true);
        }

        /// <summary>
        /// Gets the latest hash value of the file. Returns from cache if unchanged; otherwise, recalculates and updates it.
        /// </summary>
        /// <returns>
        /// changed
        /// </returns>
        public bool IsChanged(string relativePath, string filePath)
        {
            System.Diagnostics.Debug.Assert(File.Exists(filePath));

            FileInfo fileInfo = new FileInfo(filePath);
            long currentSize = fileInfo.Length;
            string currentModified = fileInfo.LastWriteTimeUtc.ToString("o"); // ISO 8601 format

            CachedFile cachedRecord = collection_.FindOne(x => 
                x.FilePath == relativePath && 
                x.FileSize == currentSize && 
                x.LastModified == currentModified);

            if (cachedRecord == null)
            {
                return true;
            }

            string computedHash = ComputeHash(filePath);
            return cachedRecord.FileHash != computedHash;
        }

        /// <summary>
        /// Gets the latest hash value of the file. Returns from cache if unchanged; otherwise, recalculates and updates it.
        /// </summary>
        /// <returns>
        /// (changed, hash)
        /// </returns>
        public void UpdateHash(string relativePath, string filePath)
        {
            System.Diagnostics.Debug.Assert(File.Exists(filePath));

            FileInfo fileInfo = new FileInfo(filePath);
            long currentSize = fileInfo.Length;
            string currentModified = fileInfo.LastWriteTimeUtc.ToString("o"); // ISO 8601 format
            string computedHash = ComputeHash(filePath);
            var newRecord = new CachedFile
            {
                FilePath = relativePath,
                FileSize = currentSize,
                LastModified = currentModified,
                FileHash = computedHash,
                CachedAt = DateTime.UtcNow.ToString("o")
            };
            // Upsert automatically inserts or updates based on the primary key (FilePath)
            collection_.Upsert(newRecord);
        }

        /// <summary>
        /// Gets the latest hash value of the file. Returns from cache if unchanged; otherwise, recalculates and updates it.
        /// </summary>
        /// <returns>
        /// (changed, hash)
        /// </returns>
        public Tuple<bool, string> GetOrUpdateHash(string relativePath, string filePath)
        {
            System.Diagnostics.Debug.Assert(File.Exists(filePath));

            FileInfo fileInfo = new FileInfo(filePath);
            long currentSize = fileInfo.Length;
            string currentModified = fileInfo.LastWriteTimeUtc.ToString("o"); // ISO 8601 format

            // 1. Check cache (verify if size and last modified date match)
            // Query using LINQ expressions
            CachedFile cachedRecord = collection_.FindOne(x => 
                x.FilePath == relativePath && 
                x.FileSize == currentSize && 
                x.LastModified == currentModified);

            if (cachedRecord != null)
            {
                // If size and modified date are unchanged, skip the heavy hash calculation and return immediately
                return Tuple.Create<bool, string>(false, cachedRecord.FileHash);
            }

            // 2. Recalculate hash if not in cache or if the file has been modified
            string computedHash = ComputeHash(filePath);

            // 3. Upsert cache info (insert or update)
            var newRecord = new CachedFile
            {
                FilePath = relativePath,
                FileSize = currentSize,
                LastModified = currentModified,
                FileHash = computedHash,
                CachedAt = DateTime.UtcNow.ToString("o")
            };

            // Upsert automatically inserts or updates based on the primary key (FilePath)
            collection_.Upsert(newRecord);

            return Tuple.Create<bool, string>(true, computedHash);
        }

        /// <summary>
        /// Gets the latest hash value of the file. Returns from cache.
        /// </summary>
        public string GeHash(string relativePath)
        {
            // 1. Check cache (verify if size and last modified date match)
            // Query using LINQ expressions
            var cachedRecord = collection_.FindOne(x => 
                x.FilePath == relativePath);

            if (cachedRecord != null)
            {
                // If size and modified date are unchanged, skip the heavy hash calculation and return immediately
                return cachedRecord.FileHash;
            }
            return string.Empty;
        }

        private static string ComputeHash(string filePath)
        {
            using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                HashAlgorithm murmur128 = MurmurHash.Create128(managed: true);
                byte[] hash = murmur128.ComputeHash(fileStream);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("X2"));
                }
                return sb.ToString();
            }
        }

        public void Dispose()
        {
            db_?.Dispose();
        }
    }
}
