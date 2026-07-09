using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Wisp.Models;

namespace Wisp.Services
{
    public class DatabaseService
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public DatabaseService()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp");
            if (!Directory.Exists(appData))
            {
                Directory.CreateDirectory(appData);
            }

            _dbPath = Path.Combine(appData, "clips.db");
            _connectionString = $"Data Source={_dbPath}";
            Initialize();
        }

        private void Initialize()
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = @"
                    CREATE TABLE IF NOT EXISTS Clips (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        FilePath TEXT NOT NULL,
                        ThumbnailPath TEXT NOT NULL,
                        Filename TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        DurationSeconds REAL NOT NULL,
                        FileSizeBytes INTEGER NOT NULL,
                        GameName TEXT NOT NULL DEFAULT '',
                        IsFavorite INTEGER NOT NULL DEFAULT 0,
                        ProtectedFromDeletion INTEGER NOT NULL DEFAULT 0,
                        SystemTrackPath TEXT NOT NULL DEFAULT '',
                        MicTrackPath TEXT NOT NULL DEFAULT '',
                        SocialTrackPath TEXT NOT NULL DEFAULT '',
                        KillMarkers TEXT NOT NULL DEFAULT ''
                    );";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.ExecuteNonQuery();
                }

                // Migration: add GameName to libraries created before the auto-tag feature.
                if (!ColumnExists(conn, "Clips", "GameName"))
                {
                    using (var cmd = new SqliteCommand("ALTER TABLE Clips ADD COLUMN GameName TEXT NOT NULL DEFAULT ''", conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }

                // Migration: add IsFavorite to libraries created before the favorites feature.
                if (!ColumnExists(conn, "Clips", "IsFavorite"))
                {
                    using (var cmd = new SqliteCommand("ALTER TABLE Clips ADD COLUMN IsFavorite INTEGER NOT NULL DEFAULT 0", conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }

                // Migration: add ProtectedFromDeletion for the auto-deletion "keep this clip" shield.
                if (!ColumnExists(conn, "Clips", "ProtectedFromDeletion"))
                {
                    using (var cmd = new SqliteCommand("ALTER TABLE Clips ADD COLUMN ProtectedFromDeletion INTEGER NOT NULL DEFAULT 0", conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }

                // Migration: add separate-audio-track paths for the in-player live mixer.
                if (!ColumnExists(conn, "Clips", "SystemTrackPath"))
                {
                    using (var cmd = new SqliteCommand("ALTER TABLE Clips ADD COLUMN SystemTrackPath TEXT NOT NULL DEFAULT ''", conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
                if (!ColumnExists(conn, "Clips", "MicTrackPath"))
                {
                    using (var cmd = new SqliteCommand("ALTER TABLE Clips ADD COLUMN MicTrackPath TEXT NOT NULL DEFAULT ''", conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
                if (!ColumnExists(conn, "Clips", "SocialTrackPath"))
                {
                    using (var cmd = new SqliteCommand("ALTER TABLE Clips ADD COLUMN SocialTrackPath TEXT NOT NULL DEFAULT ''", conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
                
                // Migration: add Tags for library organization
                if (!ColumnExists(conn, "Clips", "Tags"))
                {
                    using (var cmd = new SqliteCommand("ALTER TABLE Clips ADD COLUMN Tags TEXT NOT NULL DEFAULT ''", conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }

                // Migration: add ChainMarkers (CSV of clip-relative tap offsets) for clip chaining.
                if (!ColumnExists(conn, "Clips", "ChainMarkers"))
                {
                    using (var cmd = new SqliteCommand("ALTER TABLE Clips ADD COLUMN ChainMarkers TEXT NOT NULL DEFAULT ''", conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }

                // Migration: add KillMarkers (CSV of clip-relative kill offsets) for kill detection.
                if (!ColumnExists(conn, "Clips", "KillMarkers"))
                {
                    using (var cmd = new SqliteCommand("ALTER TABLE Clips ADD COLUMN KillMarkers TEXT NOT NULL DEFAULT ''", conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }

                // Create TagDefinitions table for color-coded tags
                string sqlTags = @"
                    CREATE TABLE IF NOT EXISTS TagDefinitions (
                        Name TEXT PRIMARY KEY,
                        ColorHex TEXT NOT NULL
                    );";
                using (var cmd = new SqliteCommand(sqlTags, conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static bool ColumnExists(SqliteConnection conn, string table, string column)
        {
            using (var cmd = new SqliteCommand($"PRAGMA table_info({table})", conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        public List<Clip> GetAllClips()
        {
            var tagDefs = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var td in GetAllTagDefinitions())
                {
                    tagDefs[td.Name] = td;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load tag definitions for resolution: {ex.Message}");
            }

            var clips = new List<Clip>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = "SELECT Id, FilePath, ThumbnailPath, Filename, CreatedAt, DurationSeconds, FileSizeBytes, GameName, IsFavorite, SystemTrackPath, MicTrackPath, SocialTrackPath, Tags, ChainMarkers, ProtectedFromDeletion, KillMarkers FROM Clips ORDER BY CreatedAt DESC";
                using (var cmd = new SqliteCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string tagsCsv = reader.IsDBNull(12) ? "" : reader.GetString(12);
                        var tagList = new List<TagDefinition>();
                        if (!string.IsNullOrWhiteSpace(tagsCsv))
                        {
                            var parts = tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var p in parts)
                            {
                                string name = p.Trim();
                                if (string.IsNullOrEmpty(name)) continue;
                                if (tagDefs.TryGetValue(name, out var td))
                                {
                                    tagList.Add(td);
                                }
                                else
                                {
                                    // Fallback for tags created externally or in old files
                                    tagList.Add(new TagDefinition { Name = name, ColorHex = "#00F2FF" });
                                }
                            }
                        }

                        clips.Add(new Clip
                        {
                            Id = reader.GetInt32(0),
                            FilePath = reader.GetString(1),
                            ThumbnailPath = reader.GetString(2),
                            Filename = reader.GetString(3),
                            CreatedAt = DateTime.Parse(reader.GetString(4)),
                            DurationSeconds = reader.GetDouble(5),
                            FileSizeBytes = reader.GetInt64(6),
                            GameName = reader.IsDBNull(7) ? "" : reader.GetString(7),
                            IsFavorite = !reader.IsDBNull(8) && reader.GetInt64(8) != 0,
                            SystemTrackPath = reader.IsDBNull(9) ? "" : reader.GetString(9),
                            MicTrackPath = reader.IsDBNull(10) ? "" : reader.GetString(10),
                            SocialTrackPath = reader.IsDBNull(11) ? "" : reader.GetString(11),
                            Tags = tagsCsv,
                            TagList = tagList,
                            ChainMarkers = reader.IsDBNull(13) ? "" : reader.GetString(13),
                            ProtectedFromDeletion = !reader.IsDBNull(14) && reader.GetInt64(14) != 0,
                            KillMarkers = reader.IsDBNull(15) ? "" : reader.GetString(15)
                        });
                    }
                }
            }
            return clips;
        }

        public void AddClip(Clip clip)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = @"
                    INSERT INTO Clips (FilePath, ThumbnailPath, Filename, CreatedAt, DurationSeconds, FileSizeBytes, GameName, IsFavorite, ProtectedFromDeletion, SystemTrackPath, MicTrackPath, SocialTrackPath, Tags, ChainMarkers, KillMarkers)
                    VALUES ($filePath, $thumbnailPath, $filename, $createdAt, $duration, $size, $gameName, $isFavorite, $protected, $systemTrack, $micTrack, $socialTrack, $tags, $chainMarkers, $killMarkers)";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("$filePath", clip.FilePath);
                    cmd.Parameters.AddWithValue("$thumbnailPath", clip.ThumbnailPath);
                    cmd.Parameters.AddWithValue("$filename", clip.Filename);
                    cmd.Parameters.AddWithValue("$createdAt", clip.CreatedAt.ToString("o")); // ISO 8601 string
                    cmd.Parameters.AddWithValue("$duration", clip.DurationSeconds);
                    cmd.Parameters.AddWithValue("$size", clip.FileSizeBytes);
                    cmd.Parameters.AddWithValue("$gameName", clip.GameName ?? "");
                    cmd.Parameters.AddWithValue("$isFavorite", clip.IsFavorite ? 1 : 0);
                    cmd.Parameters.AddWithValue("$protected", clip.ProtectedFromDeletion ? 1 : 0);
                    cmd.Parameters.AddWithValue("$systemTrack", clip.SystemTrackPath ?? "");
                    cmd.Parameters.AddWithValue("$micTrack", clip.MicTrackPath ?? "");
                    cmd.Parameters.AddWithValue("$socialTrack", clip.SocialTrackPath ?? "");
                    cmd.Parameters.AddWithValue("$tags", clip.Tags ?? "");
                    cmd.Parameters.AddWithValue("$chainMarkers", clip.ChainMarkers ?? "");
                    cmd.Parameters.AddWithValue("$killMarkers", clip.KillMarkers ?? "");
                    cmd.ExecuteNonQuery();
                }

                // Surface the new row id so callers can update/replace this clip later (e.g. chaining,
                // which deletes the short first-tap clip once the longer stitched one is written).
                using (var idCmd = new SqliteCommand("SELECT last_insert_rowid()", conn))
                {
                    clip.Id = (int)(long)(idCmd.ExecuteScalar() ?? 0L);
                }
            }
        }

        public void UpdateClipPaths(int id, string newFilePath, string newFilename)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = "UPDATE Clips SET FilePath = $filePath, Filename = $filename WHERE Id = $id";
                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("$filePath", newFilePath);
                    cmd.Parameters.AddWithValue("$filename", newFilename);
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void SetFavorite(int id, bool isFavorite)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = "UPDATE Clips SET IsFavorite = $fav WHERE Id = $id";
                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("$fav", isFavorite ? 1 : 0);
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void SetProtectedFromDeletion(int id, bool isProtected)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = "UPDATE Clips SET ProtectedFromDeletion = $val WHERE Id = $id";
                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("$val", isProtected ? 1 : 0);
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void UpdateTags(int id, string tags)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = "UPDATE Clips SET Tags = $tags WHERE Id = $id";
                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("$tags", tags ?? "");
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public List<TagDefinition> GetAllTagDefinitions()
        {
            var list = new List<TagDefinition>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = "SELECT Name, ColorHex FROM TagDefinitions ORDER BY Name ASC";
                using (var cmd = new SqliteCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        list.Add(new TagDefinition
                        {
                            Name = reader.GetString(0),
                            ColorHex = reader.GetString(1)
                        });
                    }
                }
            }
            return list;
        }

        public void SaveTagDefinition(TagDefinition tag)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = "INSERT OR REPLACE INTO TagDefinitions (Name, ColorHex) VALUES ($name, $colorHex)";
                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("$name", tag.Name.Trim());
                    cmd.Parameters.AddWithValue("$colorHex", tag.ColorHex);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void DeleteTagDefinition(string name)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = "DELETE FROM TagDefinitions WHERE Name = $name";
                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("$name", name.Trim());
                    cmd.ExecuteNonQuery();
                }
            }

            // Also clean up this tag from all clips' CSV list
            var clips = GetAllClips();
            foreach (var clip in clips)
            {
                if (string.IsNullOrWhiteSpace(clip.Tags)) continue;
                var parts = clip.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                      .Select(t => t.Trim())
                                      .Where(t => !string.Equals(t, name, StringComparison.OrdinalIgnoreCase))
                                      .ToList();
                string newTags = string.Join(", ", parts);
                if (newTags != clip.Tags)
                {
                    UpdateTags(clip.Id, newTags);
                }
            }
        }

        public void RenameTagDefinition(string oldName, string newName)
        {
            oldName = oldName.Trim();
            newName = newName.Trim();
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                
                // Get the old definition if it exists
                string color = "#00F2FF";
                string selectSql = "SELECT ColorHex FROM TagDefinitions WHERE Name = $oldName";
                using (var cmd = new SqliteCommand(selectSql, conn))
                {
                    cmd.Parameters.AddWithValue("$oldName", oldName);
                    var val = cmd.ExecuteScalar();
                    if (val != null) color = val.ToString() ?? color;
                }

                // Delete old
                string deleteSql = "DELETE FROM TagDefinitions WHERE Name = $oldName";
                using (var cmd = new SqliteCommand(deleteSql, conn))
                {
                    cmd.Parameters.AddWithValue("$oldName", oldName);
                    cmd.ExecuteNonQuery();
                }

                // Insert new with same color
                string insertSql = "INSERT OR REPLACE INTO TagDefinitions (Name, ColorHex) VALUES ($newName, $color)";
                using (var cmd = new SqliteCommand(insertSql, conn))
                {
                    cmd.Parameters.AddWithValue("$newName", newName);
                    cmd.Parameters.AddWithValue("$color", color);
                    cmd.ExecuteNonQuery();
                }
            }

            // Also update all clips' CSV tags
            var clips = GetAllClips();
            foreach (var clip in clips)
            {
                if (string.IsNullOrWhiteSpace(clip.Tags)) continue;
                var parts = clip.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                      .Select(t => t.Trim())
                                      .ToList();
                bool modified = false;
                for (int i = 0; i < parts.Count; i++)
                {
                    if (string.Equals(parts[i], oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        parts[i] = newName;
                        modified = true;
                    }
                }
                if (modified)
                {
                    // Clean duplicate tag names if they somehow arise
                    var distinctParts = parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    string newTags = string.Join(", ", distinctParts);
                    UpdateTags(clip.Id, newTags);
                }
            }
        }

        public void EnsureDefaultTagsExist(List<Clip> clips)
        {
            var existingTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var td in GetAllTagDefinitions())
                {
                    existingTags.Add(td.Name);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load existing tags: {ex.Message}");
                return;
            }

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                foreach (var clip in clips)
                {
                    if (string.IsNullOrWhiteSpace(clip.Tags)) continue;
                    var tags = clip.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim());
                    foreach (var tag in tags)
                    {
                        if (!string.IsNullOrEmpty(tag) && !existingTags.Contains(tag))
                        {
                            string sql = "INSERT OR IGNORE INTO TagDefinitions (Name, ColorHex) VALUES ($name, $color)";
                            using (var cmd = new SqliteCommand(sql, conn))
                            {
                                cmd.Parameters.AddWithValue("$name", tag);
                                cmd.Parameters.AddWithValue("$color", "#00F2FF"); // Default Wisp Cyan
                                cmd.ExecuteNonQuery();
                            }
                            existingTags.Add(tag);
                        }
                    }
                }
            }
        }

        public void DeleteClip(int id)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                string sql = "DELETE FROM Clips WHERE Id = $id";
                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Removes a clip from the library AND deletes its on-disk artifacts (video, thumbnail, per-source
        /// audio sidecars). Used when a chained clip replaces the short first-tap clip it grew out of.
        /// Best-effort on the files (a file the player still holds open is skipped); the DB row always goes.
        /// </summary>
        public void DeleteClipAndFiles(Clip clip)
        {
            if (clip == null) return;
            try { if (!string.IsNullOrEmpty(clip.FilePath) && File.Exists(clip.FilePath)) File.Delete(clip.FilePath); } catch { }
            try { if (!string.IsNullOrEmpty(clip.ThumbnailPath) && File.Exists(clip.ThumbnailPath)) File.Delete(clip.ThumbnailPath); } catch { }
            try { if (!string.IsNullOrEmpty(clip.SystemTrackPath) && File.Exists(clip.SystemTrackPath)) File.Delete(clip.SystemTrackPath); } catch { }
            try { if (!string.IsNullOrEmpty(clip.MicTrackPath) && File.Exists(clip.MicTrackPath)) File.Delete(clip.MicTrackPath); } catch { }
            try { if (!string.IsNullOrEmpty(clip.SocialTrackPath) && File.Exists(clip.SocialTrackPath)) File.Delete(clip.SocialTrackPath); } catch { }
            DeleteClip(clip.Id);
        }

        public void CleanOrphanedClips()
        {
            var clips = GetAllClips();
            foreach (var clip in clips)
            {
                if (!File.Exists(clip.FilePath))
                {
                    DeleteClip(clip.Id);
                    // Also delete orphaned thumbnail and audio-track sidecars if they exist
                    if (File.Exists(clip.ThumbnailPath))
                    {
                        try { File.Delete(clip.ThumbnailPath); } catch { }
                    }
                    try { if (!string.IsNullOrEmpty(clip.SystemTrackPath) && File.Exists(clip.SystemTrackPath)) File.Delete(clip.SystemTrackPath); } catch { }
                    try { if (!string.IsNullOrEmpty(clip.MicTrackPath) && File.Exists(clip.MicTrackPath)) File.Delete(clip.MicTrackPath); } catch { }
                    try { if (!string.IsNullOrEmpty(clip.SocialTrackPath) && File.Exists(clip.SocialTrackPath)) File.Delete(clip.SocialTrackPath); } catch { }
                }
            }
        }

        public void CleanupOldClips(int daysToKeep)
        {
            var cutoff = DateTime.Now.AddDays(-daysToKeep);
            var clips = GetAllClips();
            foreach (var clip in clips)
            {
                if (!clip.IsKept && clip.CreatedAt < cutoff)
                {
                    try { if (File.Exists(clip.FilePath)) File.Delete(clip.FilePath); } catch { }
                    try { if (!string.IsNullOrEmpty(clip.ThumbnailPath) && File.Exists(clip.ThumbnailPath)) File.Delete(clip.ThumbnailPath); } catch { }
                    try { if (!string.IsNullOrEmpty(clip.SystemTrackPath) && File.Exists(clip.SystemTrackPath)) File.Delete(clip.SystemTrackPath); } catch { }
                    try { if (!string.IsNullOrEmpty(clip.MicTrackPath) && File.Exists(clip.MicTrackPath)) File.Delete(clip.MicTrackPath); } catch { }
                    try { if (!string.IsNullOrEmpty(clip.SocialTrackPath) && File.Exists(clip.SocialTrackPath)) File.Delete(clip.SocialTrackPath); } catch { }
                    
                    DeleteClip(clip.Id);
                }
            }
        }

        /// <summary>Sum of every clip's file size - the figure the storage budget is measured against.</summary>
        public long GetTotalClipBytes()
        {
            long total = 0;
            foreach (var c in GetAllClips()) total += c.FileSizeBytes;
            return total;
        }

        /// <summary>
        /// Trims the library to a storage budget by deleting the OLDEST non-kept clips (and their files)
        /// until the total is at or under maxBytes, or nothing deletable remains. Favorited/protected clips
        /// (Clip.IsKept) are never touched. Returns how many clips were removed and the bytes that freed.
        /// </summary>
        public (int removedCount, long removedBytes) EnforceStorageCap(long maxBytes)
        {
            if (maxBytes < 0) maxBytes = 0;
            var clips = GetAllClips();
            long total = clips.Sum(c => c.FileSizeBytes);
            if (total <= maxBytes) return (0, 0);

            int removed = 0; long freed = 0;
            foreach (var clip in clips.OrderBy(c => c.CreatedAt)) // oldest first
            {
                if (total <= maxBytes) break;
                if (clip.IsKept) continue;
                long size = clip.FileSizeBytes;
                DeleteClipAndFiles(clip);
                total -= size; freed += size; removed++;
            }
            return (removed, freed);
        }

        /// <summary>
        /// Dry run of <see cref="EnforceStorageCap"/>: how many clips (and bytes) WOULD be removed to reach
        /// maxBytes, deleting nothing. Drives the confirmation prompt before a destructive prune.
        /// </summary>
        public (int count, long bytes) PreviewStorageCapPrune(long maxBytes)
        {
            if (maxBytes < 0) maxBytes = 0;
            var clips = GetAllClips();
            long total = clips.Sum(c => c.FileSizeBytes);
            if (total <= maxBytes) return (0, 0);

            int count = 0; long bytes = 0;
            foreach (var clip in clips.OrderBy(c => c.CreatedAt))
            {
                if (total <= maxBytes) break;
                if (clip.IsKept) continue;
                total -= clip.FileSizeBytes;
                bytes += clip.FileSizeBytes;
                count++;
            }
            return (count, bytes);
        }
    }
}
