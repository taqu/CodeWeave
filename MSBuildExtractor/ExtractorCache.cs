using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace MSBuild.CompileCommands.Extractor
{
    /// <summary>
    /// SQLite-backed delta cache for extracted compile commands.
    /// <para>
    /// Keyed by <c>(absolute .vcxproj path, configuration, platform)</c> encoded as a
    /// single <c>VcxprojPath</c> primary key so the schema stays minimal.  A SHA-256
    /// hash of the project file drives the freshness check; when the hash matches the
    /// stored value the MSBuild evaluation pipeline is skipped entirely and the
    /// previously serialised commands are returned from the database.
    /// </para>
    /// <para>
    /// Each project's result is committed with an atomic <c>UPSERT</c> immediately
    /// after extraction, so only the rows that changed are rewritten.  This avoids the
    /// full-file-rewrite bottleneck of the previous JSON cache for large solutions.
    /// </para>
    /// <para>
    /// The database is stored as <c>.extractor_cache.db</c> inside the output
    /// directory.  WAL journal mode is enabled for better write concurrency.
    /// </para>
    /// </summary>
    public sealed class ExtractorCache : IDisposable
    {
        // ---- schema -----------------------------------------------------------

        private const string CreateTableSql = """
            CREATE TABLE IF NOT EXISTS ProjectCache (
                VcxprojPath      TEXT PRIMARY KEY,
                VcxprojHash      TEXT NOT NULL,
                LastExtractedUtc TEXT NOT NULL,
                CommandsJson     TEXT NOT NULL DEFAULT ''
            )
            """;

        private const string UpsertSql = """
            INSERT INTO ProjectCache (VcxprojPath, VcxprojHash, LastExtractedUtc, CommandsJson)
            VALUES ($path, $hash, $ts, $json)
            ON CONFLICT(VcxprojPath) DO UPDATE SET
                VcxprojHash      = excluded.VcxprojHash,
                LastExtractedUtc = excluded.LastExtractedUtc,
                CommandsJson     = excluded.CommandsJson
            """;

        private const string SelectSql = """
            SELECT VcxprojHash, CommandsJson
            FROM   ProjectCache
            WHERE  VcxprojPath = $path
            """;

        // ---- Hashing
        private Murmur.Murmur128 murmur_ = Murmur.MurmurHash.Create128(123);

        // ---- JSON intermediary (avoids [JsonIgnore] on CompileCommand) --------

        private sealed record CachedCommand(
            string File,
            string[] Arguments,
            string Directory,
            string? ProjectPath,
            string? ProjectName,
            string? Configuration,
            string? Platform);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // ---- state ------------------------------------------------------------

        private readonly SqliteConnection _conn;
        private bool _disposed;

        // ---- construction / tear-down ----------------------------------------

        public ExtractorCache(string directory)
        {
            Directory.CreateDirectory(directory);
            var dbPath = Path.Combine(directory, ".extractor_cache.db");
            _conn = new SqliteConnection($"Data Source={dbPath}");
            _conn.Open();

            // WAL gives better write throughput; NORMAL sync is safe and fast.
            ExecuteNonQuery("PRAGMA journal_mode=WAL");
            ExecuteNonQuery("PRAGMA synchronous=NORMAL");

            InitializeSchema();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _conn.Dispose();
        }

        // ---- public API -------------------------------------------------------

        /// <summary>
        /// Returns <see langword="true"/> and populates <paramref name="commands"/>
        /// when the database contains a row whose stored hash still matches the
        /// current .vcxproj file — meaning no rebuild is needed.
        /// </summary>
        public bool TryGetCachedCommands(
            string projectPath,
            string configuration,
            string platform,
            out List<CompileCommand>? commands)
        {
            commands = null;
            string key = MakeKey(projectPath, configuration, platform);

            string currentHash;
            try { currentHash = ComputeFileHash(projectPath); }
            catch { return false; }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = SelectSql;
            cmd.Parameters.AddWithValue("$path", key);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return false;

            var storedHash = reader.GetString(0);
            if (!string.Equals(storedHash, currentHash, StringComparison.Ordinal))
                return false;

            var json = reader.GetString(1);
            if (string.IsNullOrEmpty(json)) return false;

            try
            {
                CachedCommand[] cached = JsonSerializer.Deserialize<CachedCommand[]>(json, JsonOptions);
                if (cached == null) return false;
                commands = cached
                    .Select(c => new CompileCommand(
                        c.File, c.Arguments, c.Directory,
                        c.ProjectPath, c.ProjectName, c.Configuration, c.Platform))
                    .ToList();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Immediately commits the extraction result for one project via an atomic
        /// UPSERT — only this project's row is written; no other rows are touched.
        /// </summary>
        public void CacheCommands(
            string projectPath,
            string configuration,
            string platform,
            List<CompileCommand> commands)
        {
            string hash;
            try { hash = ComputeFileHash(projectPath); }
            catch { return; }

            var cached = commands.Select(c => new CachedCommand(
                c.File, c.Arguments, c.Directory,
                c.ProjectPath, c.ProjectName, c.Configuration, c.Platform));

            string json;
            try { json = JsonSerializer.Serialize(cached, JsonOptions); }
            catch { return; }

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = UpsertSql;
            cmd.Parameters.AddWithValue("$path", MakeKey(projectPath, configuration, platform));
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$ts",   DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$json", json);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// No-op: UPSERTs are committed immediately in <see cref="CacheCommands"/>.
        /// Kept for API compatibility with call sites that invoke <c>Save()</c>.
        /// </summary>
        public void Save() { }

        // ---- private helpers --------------------------------------------------

        private void InitializeSchema()
        {
            ExecuteNonQuery(CreateTableSql);
        }

        private void ExecuteNonQuery(string sql)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Encodes (path, config, platform) into a single primary-key string.
        /// Uses <c>|||</c> as separator — a sequence that cannot appear in file paths.
        /// </summary>
        private static string MakeKey(string projectPath, string configuration, string platform) =>
            $"{Path.GetFullPath(projectPath).ToLowerInvariant()}|||{configuration.ToLowerInvariant()}|||{platform.ToLowerInvariant()}";

        private string ComputeFileHash(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return BitConverter.ToString(murmur_.ComputeHash(stream)).Replace("-", "");
        }
    }
}
