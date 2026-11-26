using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;
using Timer = System.Timers.Timer;

namespace AMWin_RichPresence {
    /// <summary>
    /// Reads what other players are playing from Windows' own media controls (the same information the volume flyout shows):
    /// Spotify, browsers (YouTube Music, Spotify Web, Tidal...) and anything else that reports to Windows. Apple Music is left to
    /// AppleMusicClientScraper, which reads more from it. Added for Homulily Desktop (2026-11-26, Spotify support).
    /// Every refresh hands back, for each source, the session to follow: the one playing, or else one that is paused.
    /// </summary>
    internal class MediaSessionReader {
        public delegate void RefreshHandler(Dictionary<string, AppleMusicInfo?> bySource);

        public static readonly string[] Sources = ["spotify", "musicbee", "foobar", "browser", "other"];

        private readonly Timer timer;
        private readonly RefreshHandler handler;
        private readonly Logger? logger;
        private GlobalSystemMediaTransportControlsSessionManager? manager;
        private bool busy;

        public MediaSessionReader(int refreshPeriodInSec, RefreshHandler handler, Logger? logger = null) {
            this.handler = handler;
            this.logger = logger;
            timer = new Timer(refreshPeriodInSec * 1000);
            timer.Elapsed += async (_, _) => await Refresh();
            timer.AutoReset = true;
            timer.Start();
        }

        /// <summary>Which of our sources a player belongs to, or null for Apple Music (read elsewhere).</summary>
        public static string? SourceOf(string appId) {
            var id = (appId ?? "").ToLowerInvariant();
            if (id.Contains("applemusic") || id.StartsWith("appleinc.")) return null;
            if (id.Contains("spotify")) return "spotify";
            if (id.Contains("musicbee")) return "musicbee";   // reports through the mb_MediaControl plugin
            if (id.Contains("foobar")) return "foobar";       // reports through the foo_mediacontrol component
            string[] browsers = ["chrome", "msedge", "firefox", "308046b0af4a39cb", "opera", "brave", "vivaldi", "arc"];
            if (browsers.Any(b => id.Contains(b))) return "browser";
            return "other";
        }

        private async Task Refresh() {
            if (busy) return;
            busy = true;
            try {
                manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var found = Sources.ToDictionary(s => s, s => (AppleMusicInfo?)null);
                foreach (var session in manager.GetSessions()) {
                    var source = SourceOf(session.SourceAppUserModelId);
                    if (source == null) continue;
                    var info = await Read(session);
                    if (info == null) continue;
                    var current = found[source];
                    if (current == null || (current.IsPaused && !info.IsPaused)) found[source] = info;
                    if (source is "musicbee" or "foobar") FullMode.Add(source);
                }
                // MusicBee and foobar2000 without their add-on: read the song from the window title instead (basic mode)
                found["musicbee"] ??= FromWindowTitle("MusicBee", ParseMusicBeeTitle);
                found["foobar"] ??= FromWindowTitle("foobar2000", ParseFoobarTitle);
                handler(found);
            } catch (Exception ex) {
                logger?.Log($"[Media sessions] Refresh failed: {ex.Message}");
            } finally {
                busy = false;
            }
        }

        /// <summary>Players seen reporting through Windows' media controls this session (their add-on works).</summary>
        public static readonly HashSet<string> FullMode = new();

        private static AppleMusicInfo? FromWindowTitle(string processName, Func<string, AppleMusicInfo?> parse) {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName)) {
                try {
                    var title = p.MainWindowTitle;
                    if (!string.IsNullOrWhiteSpace(title)) return parse(title.Trim());
                } catch { }
            }
            return null;
        }

        private static readonly System.Text.RegularExpressions.Regex FoobarSuffix = new(@"^(?<body>.+?)\s*\[foobar2000[^\]]*\]$");
        private static readonly System.Text.RegularExpressions.Regex FoobarAlbum = new(@"^\[(?<album>[^\]]*?)(?: CD\d+)?(?: #\d+)?\] (?<title>.+)$");

        /// <summary>
        /// foobar2000's default title while playing: "[Album Artist - ][[Album[ CD1][ #01]] ]Title[ // Track Artist] [foobar2000]".
        /// When stopped it is just "foobar2000 v2.x", which has no song. Pausing does not change it, so a paused song still reads
        /// as playing in this mode; the two-minute rule for songs of unknown length keeps that from counting quickly.
        /// </summary>
        public static AppleMusicInfo? ParseFoobarTitle(string title) {
            var m = FoobarSuffix.Match(title);
            if (!m.Success) return null;
            var body = m.Groups["body"].Value;
            var dash = body.IndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0) return null;
            var artist = body[..dash].Trim();
            var rest = body[(dash + 3)..].Trim();
            var album = "";
            var a = FoobarAlbum.Match(rest);
            if (a.Success) {
                album = a.Groups["album"].Value.Trim();
                rest = a.Groups["title"].Value.Trim();
            }
            var slashes = rest.IndexOf(" // ", StringComparison.Ordinal);
            if (slashes > 0) {
                artist = rest[(slashes + 4)..].Trim();  // the track's own artist beats the album artist
                rest = rest[..slashes].Trim();
            }
            return rest == "" || artist == "" ? null : new AppleMusicInfo(rest, "", album, artist) { IsPaused = false };
        }

        /// <summary>MusicBee shows "Artist - Title" (optionally followed by " - MusicBee") while playing, and "MusicBee" when not.</summary>
        public static AppleMusicInfo? ParseMusicBeeTitle(string title) {
            foreach (var suffix in new[] { " - MusicBee", " | MusicBee" }) {
                if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) title = title[..^suffix.Length];
            }
            if (title.Equals("MusicBee", StringComparison.OrdinalIgnoreCase)) return null;
            var dash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0) return null;
            var artist = title[..dash].Trim();
            var track = title[(dash + 3)..].Trim();
            return track == "" ? null : new AppleMusicInfo(track, "", "", artist) { IsPaused = false };
        }

        // The cover per song, read once (a player may hand it over a moment after the song starts, so a miss is retried a few times).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[]? art, int tries)> artCache = new();
        private const int MaxArtBytes = 4 * 1024 * 1024;

        private static async Task<byte[]?> ReadArt(string appId, string artist, GlobalSystemMediaTransportControlsSessionMediaProperties props) {
            var key = $"{appId}\u001f{artist}\u001f{props.Title}\u001f{props.AlbumTitle}";
            if (artCache.TryGetValue(key, out var cached) && (cached.art != null || cached.tries >= 3)) return cached.art;
            byte[]? art = null;
            try {
                if (props.Thumbnail != null) {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    if (stream.Size > 0 && stream.Size <= MaxArtBytes) {
                        var bytes = new byte[stream.Size];
                        using var reader = new Windows.Storage.Streams.DataReader(stream);
                        await reader.LoadAsync((uint)stream.Size);
                        reader.ReadBytes(bytes);
                        art = bytes;
                    }
                }
            } catch { }
            if (artCache.Count > 200) artCache.Clear();
            artCache[key] = (art, (cached.tries) + 1);
            return art;
        }

        private static async Task<AppleMusicInfo?> Read(GlobalSystemMediaTransportControlsSession session) {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;
            // Spotify fills Artist; some players only fill AlbumArtist.
            var artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist : props.Artist;
            if (string.IsNullOrWhiteSpace(artist)) return null;  // a video or a podcast with no artist is not a song to scrobble
            var playback = session.GetPlaybackInfo();
            var info = new AppleMusicInfo(props.Title, "", props.AlbumTitle ?? "", artist) {
                IsPaused = playback?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            };
            info.LocalArt = await ReadArt(session.SourceAppUserModelId, artist, props);
            var timeline = session.GetTimelineProperties();
            if (timeline != null) {
                var length = (timeline.EndTime - timeline.StartTime).TotalSeconds;
                if (length >= 1) {
                    info.SongDuration = (int)length;
                    info.CurrentTime = (int)timeline.Position.TotalSeconds;
                }
            }
            return info;
        }
    }
}
