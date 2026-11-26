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

        public static readonly string[] Sources = ["spotify", "browser", "other"];

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
                }
                handler(found);
            } catch (Exception ex) {
                logger?.Log($"[Media sessions] Refresh failed: {ex.Message}");
            } finally {
                busy = false;
            }
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
