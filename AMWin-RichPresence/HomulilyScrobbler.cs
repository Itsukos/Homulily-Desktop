using Meziantou.Framework.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AMWin_RichPresence {
    public struct HomulilyCredentials : IScrobblerCredentials {
        public string server;
        public string token;
    }

    public record HomulilyStatus(string? DiscordName, string? LastfmUsername, bool CanScrobble, int Queued, IReadOnlyList<string> SelfScrobbling);

    /// <summary>
    /// Scrobbles through the Homulily Discord bot instead of logging in to Last.fm here. The bot stores each scrobble in its own
    /// copy of your history at once and sends it to Last.fm with the login you gave it (/fmset), holding it while Last.fm is down.
    /// Scrobbles the bot could not be reached for wait in a file next to the logs and are sent on the next heartbeat.
    /// Added for Homulily Desktop (a fork of AMWin-RP, GPL-3.0).
    /// </summary>
    internal class AppleMusicHomulilyScrobbler : AppleMusicScrobbler<HomulilyCredentials> {
        private const int HeartbeatSeconds = 30;   // the bot treats now playing as stale after 120 s
        private const int RetryQueueSeconds = 60;
        private const int MaxQueued = 5000;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly string QueuePath = Path.Combine(Constants.AppDataFolder, "homulily_queue.json");
        private static readonly SemaphoreSlim queueLock = new(1, 1);  // one queue file, shared by every source's scrobbler

        private HomulilyCredentials creds;
        private string? lastNowPlayingKey;
        private DateTime lastNowPlayingSent = DateTime.MinValue;
        private DateTime lastQueueAttempt = DateTime.MinValue;
        private bool clearedSent = true;

        public HomulilyStatus? LastStatus { get; private set; }

        public string Source { get; }

        /// <param name="source">applemusic, spotify, browser or other: the bot sends Apple Music at once and checks the rest
        /// against Last.fm first, in case the player already scrobbles by itself.</param>
        public AppleMusicHomulilyScrobbler(string region, Logger? logger = null, string source = "applemusic")
            : base($"Homulily {source}", region, logger) {
            Source = source;
        }

        // Windows' media controls often do not say how long a song is: two minutes of play is a safe stand-in.
        protected override int UnknownLengthWait => Source == "applemusic" ? base.UnknownLengthWait : 120;

        private bool Ready => !string.IsNullOrWhiteSpace(creds.server) && !string.IsNullOrWhiteSpace(creds.token);

        public static string GetToken() {
            var cred = CredentialManager.ReadCredential(applicationName: Constants.HomulilyCredentialTargetName);
            return cred?.Password ?? string.Empty;
        }

        public static void SaveToken(string token) {
            CredentialManager.WriteCredential(
                applicationName: Constants.HomulilyCredentialTargetName,
                userName: "",
                secret: token,
                persistence: CredentialPersistence.LocalMachine);
        }

        /// <summary>
        /// Signs in through the browser: opens the bot's /desktop/login, which logs in with Discord (and asks Last.fm to allow
        /// scrobbling when the bot has no login for you yet), then sends the token back to a one-off listener on 127.0.0.1.
        /// A raw socket rather than HttpListener, which can need admin rights for an address reservation. Null on timeout.
        /// </summary>
        public static async Task<string?> SignInWithBrowserAsync(string server, Logger? logger, CancellationToken cancel) {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try {
                var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = $"{server.TrimEnd('/')}/desktop/login?port={port}&state={state}",
                    UseShellExecute = true
                });
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                while (!timeout.IsCancellationRequested) {
                    using var conn = await listener.AcceptTcpClientAsync(timeout.Token);
                    using var stream = conn.GetStream();
                    var buffer = new byte[8192];
                    var read = await stream.ReadAsync(buffer, timeout.Token);
                    var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];  // GET /callback?... HTTP/1.1
                    var target = requestLine.Split(' ').ElementAtOrDefault(1) ?? "";
                    var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://127.0.0.1" + target).Query);
                    var ok = target.StartsWith("/callback") && query["state"] == state && !string.IsNullOrEmpty(query["token"]);
                    var page = ok ? "Homulily Desktop is signed in. You can close this tab."
                                  : "This was not the sign-in Homulily Desktop was waiting for.";
                    var html = $"<!doctype html><meta charset=utf-8><title>Homulily Desktop</title><body style='font:16px system-ui;background:#14121a;color:#ece8f4;display:grid;place-items:center;min-height:90vh'><p>{page}</p>";
                    var body = Encoding.UTF8.GetBytes(html);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, timeout.Token);
                    await stream.WriteAsync(body, timeout.Token);
                    if (ok) {
                        logger?.Log("[Homulily] Signed in through the browser");
                        return query["token"];
                    }
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                logger?.Log($"[Homulily] Browser sign-in failed: {ex.Message}");
            } finally {
                listener.Stop();
            }
            return null;
        }

        public async override Task<bool> init(HomulilyCredentials credentials) {
            creds = credentials;
            if (!Ready) {
                logger?.Log("[Homulily] No server or token set");
                return false;
            }
            LastStatus = await GetStatusAsync();
            logger?.Log(LastStatus == null ? "[Homulily] Could not sign in" : $"[Homulily] Signed in as {LastStatus.DiscordName}");
            return LastStatus != null;
        }

        public async override Task<bool> UpdateCredsAsync(HomulilyCredentials credentials) {
            logger?.Log("[Homulily] Updating credentials");
            return await init(credentials);
        }

        private HttpRequestMessage Request(HttpMethod method, string path, object? body = null) {
            var req = new HttpRequestMessage(method, creds.server.TrimEnd('/') + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.token.Trim());
            req.Headers.UserAgent.ParseAdd("HomulilyDesktop/1.0");
            if (body != null) {
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }
            return req;
        }

        public async Task<HomulilyStatus?> GetStatusAsync() {
            if (!Ready) return null;
            try {
                using var resp = await Http.SendAsync(Request(HttpMethod.Get, "/api/desktop/status"));
                if (!resp.IsSuccessStatusCode) {
                    logger?.Log($"[Homulily] Status answered {(int)resp.StatusCode}");
                    return null;
                }
                using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = json.RootElement;
                string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                return new HomulilyStatus(
                    Str("discord_name"),
                    Str("lastfm_username"),
                    root.TryGetProperty("can_scrobble", out var c) && c.ValueKind == JsonValueKind.True,
                    root.TryGetProperty("queued", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : 0,
                    root.TryGetProperty("self_scrobbling", out var self) && self.ValueKind == JsonValueKind.Array
                        ? self.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                        : new List<string>());
            } catch (Exception ex) {
                logger?.Log($"[Homulily] Status failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Called on every scraper refresh (about every 5 s), playing or not. Tells the bot what is on when the song or the pause
        /// state changes and every HeartbeatSeconds otherwise, clears it once Apple Music stops, and retries waiting scrobbles.
        /// </summary>
        public async void Heartbeat(AppleMusicInfo? info) {
            if (!Ready) return;
            try {
                if (info == null) {
                    if (!clearedSent) {
                        clearedSent = await Post("/api/desktop/nowplaying", new { artist = "", track = "" });
                        lastNowPlayingKey = null;
                    }
                } else {
                    var key = $"{info.SongArtist}\u001f{info.SongName}\u001f{info.IsPaused}\u001f{info.CoverArtUrl}";
                    if (key != lastNowPlayingKey || DateTime.UtcNow - lastNowPlayingSent > TimeSpan.FromSeconds(HeartbeatSeconds)) {
                        var ok = await Post("/api/desktop/nowplaying", new {
                            artist = info.SongArtist,
                            track = Clean(info.SongName, song: true),
                            album = Clean(info.SongAlbum, song: false),
                            duration = info.SongDuration,
                            position = info.CurrentTime,
                            paused = info.IsPaused,
                            source = Source,
                            cover = info.CoverArtUrl  // Apple Music's own artwork, so .fm has a cover while Last.fm is down
                        });
                        if (ok) {
                            lastNowPlayingKey = key;
                            lastNowPlayingSent = DateTime.UtcNow;
                            clearedSent = false;
                        }
                    }
                }
                if (DateTime.UtcNow - lastQueueAttempt > TimeSpan.FromSeconds(RetryQueueSeconds)) {
                    await FlushQueue();
                }
            } catch (Exception ex) {
                logger?.Log($"[Homulily] Heartbeat failed: {ex.Message}");
            }
        }

        private static string Clean(string value, bool song) {
            if (song) {
                return Properties.Settings.Default.LastfmCleanSongName ? SongCleaner.CleanSongName(value) : value;
            }
            return Properties.Settings.Default.LastfmCleanAlbumName ? AlbumCleaner.CleanAlbumName(value) : value;
        }

        private async Task<bool> Post(string path, object body) {
            try {
                using var resp = await Http.SendAsync(Request(HttpMethod.Post, path, body));
                if (!resp.IsSuccessStatusCode) {
                    logger?.Log($"[Homulily] {path} answered {(int)resp.StatusCode}");
                }
                return resp.IsSuccessStatusCode;
            } catch (Exception ex) {
                logger?.Log($"[Homulily] {path} failed: {ex.Message}");
                return false;
            }
        }

        // Now playing is sent by Heartbeat, which repeats it; the base class's one-off call is answered as done.
        protected override Task<bool> UpdateNowPlaying(string artist, string album, string song) => Task.FromResult(true);

        protected async override Task ScrobbleSong(string artist, string album, string song) {
            // Last.fm counts a scrobble from when the song started.
            var started = DateTimeOffset.UtcNow.AddSeconds(-elapsedSeconds).ToUnixTimeSeconds();
            await Enqueue(new QueuedScrobble(artist, album, song, started, Source));
            await FlushQueue();
        }

        private record QueuedScrobble(string artist, string album, string track, long timestamp, string? source = null);

        private async Task<List<QueuedScrobble>> ReadQueue() {
            try {
                if (!File.Exists(QueuePath)) return new();
                return JsonSerializer.Deserialize<List<QueuedScrobble>>(await File.ReadAllTextAsync(QueuePath)) ?? new();
            } catch (Exception ex) {
                logger?.Log($"[Homulily] Queue file unreadable, starting over: {ex.Message}");
                return new();
            }
        }

        private async Task WriteQueue(List<QueuedScrobble> queue) {
            Directory.CreateDirectory(Constants.AppDataFolder);
            var tmp = QueuePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(queue));
            File.Move(tmp, QueuePath, overwrite: true);
        }

        private async Task Enqueue(QueuedScrobble item) {
            await queueLock.WaitAsync();
            try {
                var queue = await ReadQueue();
                queue.Add(item);
                if (queue.Count > MaxQueued) queue = queue.Skip(queue.Count - MaxQueued).ToList();
                await WriteQueue(queue);
            } finally {
                queueLock.Release();
            }
        }

        private async Task FlushQueue() {
            lastQueueAttempt = DateTime.UtcNow;
            await queueLock.WaitAsync();
            try {
                var queue = await ReadQueue();
                // Last.fm refuses a scrobble older than 14 days, and so does the bot.
                var cutoff = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
                queue = queue.Where(q => q.timestamp > cutoff).ToList();
                while (queue.Count > 0) {
                    var batch = queue.Take(50).ToList();
                    var ok = await Post("/api/desktop/scrobble", new { scrobbles = batch });
                    if (!ok) break;
                    logger?.Log($"[Homulily] Sent {batch.Count} scrobble(s)");
                    queue.RemoveRange(0, batch.Count);
                }
                await WriteQueue(queue);
            } catch (Exception ex) {
                logger?.Log($"[Homulily] Sending the queue failed: {ex.Message}");
            } finally {
                queueLock.Release();
            }
        }
    }
}
