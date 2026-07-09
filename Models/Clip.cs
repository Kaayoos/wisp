using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace Wisp.Models
{
    public class Clip : INotifyPropertyChanged
    {
        public int Id { get; set; }
        public string FilePath { get; set; } = "";
        public string ThumbnailPath { get; set; } = "";
        public string Filename { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public double DurationSeconds { get; set; }
        public long FileSizeBytes { get; set; }

        // Foreground game/app detected when the clip was captured ("" if unknown/desktop).
        public string GameName { get; set; } = "";

        // Separate per-source audio tracks for the in-player live mixer + per-source waveforms ("" if
        // not available, e.g. clips recorded before this feature, or a source disabled at capture time).
        public string SystemTrackPath { get; set; } = "";
        public string MicTrackPath { get; set; } = "";
        public string SocialTrackPath { get; set; } = "";

        public bool HasSeparateAudio => !string.IsNullOrEmpty(SystemTrackPath) || !string.IsNullOrEmpty(MicTrackPath) || !string.IsNullOrEmpty(SocialTrackPath);

        // Clip chaining: when the hotkey is tapped several times in quick succession, the overlapping
        // buffers are stitched into ONE longer clip. ChainMarkers is a CSV of clip-relative offsets (in
        // seconds, invariant culture) marking where each tap landed - the "moments" inside this clip.
        // Empty for ordinary single-tap clips. Setting it refreshes the derived display properties so the
        // library badge and player markers update without a manual reload.
        private string _chainMarkers = "";
        public string ChainMarkers
        {
            get => _chainMarkers;
            set
            {
                if (_chainMarkers != value)
                {
                    _chainMarkers = value ?? "";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChainMarkers)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChainMarkerOffsets)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChainCount)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChained)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChainCountLabel)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChainMomentsLabel)));
                }
            }
        }

        /// <summary>The parsed marker offsets (seconds from the clip's start), sorted ascending.</summary>
        public List<double> ChainMarkerOffsets
        {
            get
            {
                var list = new List<double>();
                if (string.IsNullOrWhiteSpace(_chainMarkers)) return list;
                foreach (var part in _chainMarkers.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (double.TryParse(part.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v) && v >= 0)
                        list.Add(v);
                }
                list.Sort();
                return list;
            }
        }

        /// <summary>How many moments were chained together. A normal clip reports 1 (or 0 if unset).</summary>
        public int ChainCount => ChainMarkerOffsets.Count;

        /// <summary>True when this clip stitches together two or more chained moments.</summary>
        public bool IsChained => ChainCount >= 2;

        /// <summary>Short badge text for the library card, e.g. "3".</summary>
        public string ChainCountLabel => ChainCount.ToString();

        /// <summary>Full label for the player pill, e.g. "3 MOMENTS".</summary>
        public string ChainMomentsLabel => $"{ChainCount} MOMENTS";

        // Kill detection: CSV of clip-relative offsets (in seconds, invariant culture) marking where each
        // detected kill landed inside this clip. Same format as ChainMarkers but a separate lane - a clip
        // can have both (chained taps AND kills). Empty when kill detection was off or no kills occurred.
        private string _killMarkers = "";
        public string KillMarkers
        {
            get => _killMarkers;
            set
            {
                if (_killMarkers != value)
                {
                    _killMarkers = value ?? "";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KillMarkers)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KillMarkerOffsets)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KillCount)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasKills)));
                }
            }
        }

        /// <summary>The parsed kill offsets (seconds from the clip's start), sorted ascending.</summary>
        public List<double> KillMarkerOffsets
        {
            get
            {
                var list = new List<double>();
                if (string.IsNullOrWhiteSpace(_killMarkers)) return list;
                foreach (var part in _killMarkers.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (double.TryParse(part.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v) && v >= 0)
                        list.Add(v);
                }
                list.Sort();
                return list;
            }
        }

        /// <summary>How many detected kills this clip covers (0 for clips without kill data).</summary>
        public int KillCount => KillMarkerOffsets.Count;

        /// <summary>True when at least one detected kill landed inside this clip.</summary>
        public bool HasKills => KillCount >= 1;

        // Comma-separated list of tags
        private string _tags = "";
        public string Tags
        {
            get => _tags;
            set
            {
                if (_tags != value)
                {
                    _tags = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tags)));
                }
            }
        }

        private System.Collections.Generic.List<TagDefinition> _tagList = new();
        public System.Collections.Generic.List<TagDefinition> TagList
        {
            get => _tagList;
            set
            {
                _tagList = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagList)));
            }
        }

        // Whether the user marked this clip as a favorite. Raises change notification so the star
        // toggles in the UI without reloading the whole library.
        private bool _isFavorite;
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsKept)));
                }
            }
        }

        // Whether the user manually shielded this clip from auto-deletion ("keep" / unmark-from-deletion).
        // Favorites are always kept too (see IsKept), so a favorited clip survives a sweep regardless of this.
        private bool _protectedFromDeletion;
        public bool ProtectedFromDeletion
        {
            get => _protectedFromDeletion;
            set
            {
                if (_protectedFromDeletion != value)
                {
                    _protectedFromDeletion = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProtectedFromDeletion)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsKept)));
                }
            }
        }

        /// <summary>True when the clip is shielded from auto-deletion: a favorite OR explicitly protected.</summary>
        public bool IsKept => IsFavorite || ProtectedFromDeletion;

        public event PropertyChangedEventHandler? PropertyChanged;

        // Formatted helper properties for the WPF UI
        public string FormattedDuration => $"{Math.Round(DurationSeconds)}s";
        public string FormattedDate => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
        public string FormattedSize
        {
            get
            {
                double kb = FileSizeBytes / 1024.0;
                double mb = kb / 1024.0;
                if (mb >= 1) return $"{mb:F1} MB";
                return $"{kb:F0} KB";
            }
        }
    }
}
