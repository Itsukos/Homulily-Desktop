﻿using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Localisation = AMWin_RichPresence.Properties.Localisation;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace AMWin_RichPresence {
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application {
        private static readonly CultureInfo InitialUICulture = CultureInfo.CurrentUICulture;
        private const string OpenSettingsWindowArg = "--open-settings-window";

        private TaskbarIcon? taskbarIcon;
        private AppleMusicClientScraper amScraper;
        private AppleMusicDiscordClient discordClient;
        private AppleMusicLastFmScrobbler lastFmScrobblerClient;
        private AppleMusicListenBrainzScrobbler listenBrainzScrobblerClient;
        internal AppleMusicHomulilyScrobbler homulilyClient;
        internal readonly System.Collections.Generic.Dictionary<string, AppleMusicHomulilyScrobbler> otherPlayerClients = new();
        private MediaSessionReader? mediaSessionReader;
        private AppleMusicDiscordClient otherDiscordClient;  // presence for MusicBee, foobar2000 and others, under Homulily's app
        private volatile bool appleMusicActive;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? cover, string? songUrl)> otherPlayerArt = new();
        private static readonly string[] PresenceOrder = ["musicbee", "foobar", "browser", "other"];  // Spotify shows its own presence
        private System.Timers.Timer? updateTimer;

        /// <summary>A newer release found by the last check, shown as a bar in Settings; null when up to date.</summary>
        internal Updater.Release? AvailableUpdate { get; private set; }
        internal event Action? UpdateFound;
        private Logger? logger;

        internal static string NormalizeLanguageCode(string? languageCode) {
            return languageCode?.Trim().ToLowerInvariant() switch {
                "en" => "en",
                "de" => "de",
                "tr" => "tr",
                "ko" => "ko",
                "ja" => "ja",
                "ru" => "ru",
                "es" => "es",
                "es-MX" => "es-MX",
                _ => ""
            };
        }

        internal static void ApplyLanguagePreference() {
            var languageCode = NormalizeLanguageCode(AMWin_RichPresence.Properties.Settings.Default.Language);
            var culture = languageCode == ""
                ? InitialUICulture
                : CultureInfo.GetCultureInfo(languageCode);

            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Localisation.Culture = culture;
        }

        internal static void RestartApplication(bool openSettingsWindow = false) {
            var exePath = Constants.ExePath;
            if (!string.IsNullOrWhiteSpace(exePath)) {
                Process.Start(new ProcessStartInfo {
                    FileName = exePath,
                    Arguments = openSettingsWindow ? OpenSettingsWindowArg : "",
                    UseShellExecute = true
                });
            }
            Application.Current.Shutdown();
        }

        public LastFmCredentials lastFmCredentials {
            get {
                var creds = new LastFmCredentials();
                creds.apiKey = AMWin_RichPresence.Properties.Settings.Default.LastfmAPIKey;
                creds.apiSecret = AMWin_RichPresence.Properties.Settings.Default.LastfmSecret;
                creds.username = AMWin_RichPresence.Properties.Settings.Default.LastfmUsername;
                creds.password = SettingsWindow.GetLastFMPassword();
                return creds;
            }
        }

        public ListenBrainzCredentials listenBrainzCredentials {
            get {
                var creds = new ListenBrainzCredentials();
                creds.userToken = AMWin_RichPresence.Properties.Settings.Default.ListenBrainzUserToken;
                return creds;
            }
        }

        public HomulilyCredentials homulilyCredentials {
            get {
                var creds = new HomulilyCredentials();
                creds.server = AMWin_RichPresence.Properties.Settings.Default.HomulilyServer;
                creds.token = AppleMusicHomulilyScrobbler.GetToken();
                return creds;
            }
        }

        public App() {
            ApplyLanguagePreference();

            // make logger
            try {
                logger = new Logger();
                logger.Log("Application started");
                logger.Log($"{Environment.OSVersion}");
                logger.Log($"Using UI language: {CultureInfo.CurrentUICulture.Name}");
            } catch {
                logger = null;
            }

            // try to auto detect region
            if (AMWin_RichPresence.Properties.Settings.Default.AppleMusicRegion == "") {
                var region = RegionInfo.CurrentRegion.Name.ToLower();
                if (Constants.ValidAppleMusicRegions.Contains(region)) {
                    AMWin_RichPresence.Properties.Settings.Default.AppleMusicRegion = region;
                } else {
                    AMWin_RichPresence.Properties.Settings.Default.AppleMusicRegion = Constants.DefaultAppleMusicRegion;
                }
                AMWin_RichPresence.Properties.Settings.Default.Save();
            }
            logger?.Log($"Using region {AMWin_RichPresence.Properties.Settings.Default.AppleMusicRegion}");

            // remove what a one-button update left behind (again shortly after, in case the old copy was still closing)
            Updater.CleanUp();
            _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Updater.CleanUp());

            // check for updates now and every 6 hours
            if (AMWin_RichPresence.Properties.Settings.Default.CheckForUpdatesOnStartup) {
                _ = CheckForUpdates();
            }
            updateTimer = new System.Timers.Timer(TimeSpan.FromHours(6).TotalMilliseconds) { AutoReset = true };
            updateTimer.Elapsed += (_, _) => {
                if (AMWin_RichPresence.Properties.Settings.Default.CheckForUpdatesOnStartup) _ = CheckForUpdates(quiet: true);
            };
            updateTimer.Start();

            // start Discord RPC
            var statusDisplayOptions = (AppleMusicDiscordClient.RPStatusDisplayOptions)AMWin_RichPresence.Properties.Settings.Default.RPDisplayChoice;
            var classicalComposerAsArtist = AMWin_RichPresence.Properties.Settings.Default.ClassicalComposerAsArtist;
            var preferredDiscordClient = (DiscordClientType)AMWin_RichPresence.Properties.Settings.Default.DiscordClientPreference;
            discordClient = new(Constants.DiscordClientID, enabled: false, statusDisplayOptions: statusDisplayOptions, logger: logger, preferredClient: preferredDiscordClient);
            otherDiscordClient = new(Constants.HomulilyDiscordClientID, enabled: false, statusDisplayOptions: statusDisplayOptions, logger: logger, preferredClient: preferredDiscordClient);

            // start Last.FM scrobbler
            var amRegion = AMWin_RichPresence.Properties.Settings.Default.AppleMusicRegion;
            lastFmScrobblerClient = new AppleMusicLastFmScrobbler(region: amRegion, logger: logger);
            _ = lastFmScrobblerClient.init(lastFmCredentials);

            var lastFMApiKey = AMWin_RichPresence.Properties.Settings.Default.LastfmAPIKey;
            if (lastFMApiKey == null || lastFMApiKey == "") {
                logger?.Log("No Last.FM API key found");
            }

            // start ListenBrainz scrobbler
            listenBrainzScrobblerClient = new AppleMusicListenBrainzScrobbler(region: amRegion, logger: logger);
            _ = listenBrainzScrobblerClient.init(listenBrainzCredentials);

            // start the Homulily scrobbler (Homulily Desktop): scrobbles go through the bot, which forwards them to Last.fm
            homulilyClient = new AppleMusicHomulilyScrobbler(region: amRegion, logger: logger);
            _ = homulilyClient.init(homulilyCredentials);

            // Spotify, browsers and other players, through Windows' media controls (Homulily Desktop)
            foreach (var source in MediaSessionReader.Sources) {
                var client = new AppleMusicHomulilyScrobbler(region: amRegion, logger: logger, source: source);
                otherPlayerClients[source] = client;
                _ = client.init(homulilyCredentials);
            }
            mediaSessionReader = new MediaSessionReader(Constants.RefreshPeriod, (bySource) => {
                foreach (var info in bySource.Values) {
                    if (info != null) AddArtAndTimes(info, amRegion);
                }
                UpdateOtherPlayerPresence(bySource);
                if (!AMWin_RichPresence.Properties.Settings.Default.HomulilyEnable) return;
                foreach (var (source, info) in bySource) {
                    if (!OtherPlayerEnabled(source)) continue;
                    var client = otherPlayerClients[source];
                    if (info != null && !info.IsPaused) {
                        client.Scrobbleit(info);
                    }
                    client.Heartbeat(info);
                }
            }, logger);

            // start Apple Music scraper
            amScraper = new(lastFMApiKey, Constants.RefreshPeriod, classicalComposerAsArtist, AMWin_RichPresence.Properties.Settings.Default.AppleMusicRegion, (newInfo) => {

                // don't update scraper if Apple Music is paused or not open
                appleMusicActive = newInfo != null && !newInfo.IsPaused;
                if (appleMusicActive) otherDiscordClient.Disable();  // Apple Music wins while it plays
                if (newInfo != null && (AMWin_RichPresence.Properties.Settings.Default.ShowRPWhenMusicPaused || !newInfo.IsPaused)) {

                    // Discord RP update
                    if (AMWin_RichPresence.Properties.Settings.Default.EnableDiscordRP) {
                        discordClient.Enable();
                        discordClient.SetPresence(newInfo,
                            AMWin_RichPresence.Properties.Settings.Default.ShowAppleMusicIcon,
                            AMWin_RichPresence.Properties.Settings.Default.EnableRPCoverImages,
                            AMWin_RichPresence.Properties.Settings.Default.ShowAlbumTitle);
                    } else {
                        discordClient.Disable();
                    }

                    // Last.FM scrobble update
                    if (AMWin_RichPresence.Properties.Settings.Default.LastfmEnable) {
                        lastFmScrobblerClient.Scrobbleit(newInfo);
                    }

                    // ListenBrainz scrobble update
                    if (AMWin_RichPresence.Properties.Settings.Default.ListenBrainzEnable) {
                        listenBrainzScrobblerClient.Scrobbleit(newInfo);
                    }

                    // Homulily scrobble update (scrobbles only count while playing, like the others)
                    if (AMWin_RichPresence.Properties.Settings.Default.HomulilyEnable && !newInfo.IsPaused) {
                        homulilyClient.Scrobbleit(newInfo);
                    }
                } else {
                    discordClient.Disable();
                }

                // Homulily now playing: also told when paused or closed, so the bot stops showing it
                if (AMWin_RichPresence.Properties.Settings.Default.HomulilyEnable) {
                    homulilyClient.Heartbeat(newInfo);
                }
            }, logger);
        }

        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);
            taskbarIcon = (TaskbarIcon)FindResource("TaskbarIcon");
            if (e.Args.Contains(OpenSettingsWindowArg, StringComparer.OrdinalIgnoreCase)) {
                Current.Dispatcher.BeginInvoke(new Action(() => {
                    var settingsWindow = new SettingsWindow();
                    settingsWindow.Show();
                    settingsWindow.Focus();
                }));
            }
        }

        private void Application_Exit(object sender, ExitEventArgs e) {
            taskbarIcon?.Dispose();
            discordClient.Disable();
            otherDiscordClient.Disable();
            logger?.Log("Application finished");
        }

        internal void UpdateRPStatusDisplay(AppleMusicDiscordClient.RPStatusDisplayOptions newVal) {
            discordClient.statusDisplayOptions = newVal;
        }

        internal void UpdateDiscordClientPreference(DiscordClientType newClient) {
            discordClient.SetPreferredClient(newClient);
        }

        internal async Task<bool> UpdateLastfmCreds() {
            return await lastFmScrobblerClient.UpdateCredsAsync(lastFmCredentials);
        }

        /// <summary>
        /// Gives a song from another player the cover and song link Apple Music's own site has for it (looked up once per song,
        /// in the background, the same lookup AMWin uses), and playback times from its position, for the presence and for
        /// Homulily's .fm cover. Until the lookup finishes the presence shows the Homulily moon.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> artUploaded = new();

        private void AddArtAndTimes(AppleMusicInfo info, string region) {
            var key = $"{info.SongArtist}\u001f{info.SongName}\u001f{info.SongAlbum}";
            if (otherPlayerArt.TryGetValue(key, out var art)) {
                info.CoverArtUrl ??= art.cover;
                info.SongUrl ??= art.songUrl;
                // the player handed its own cover over a moment late: still prefer it
                if (info.LocalArt != null && artUploaded.TryAdd(key, true)) {
                    var bytes = info.LocalArt;
                    _ = Task.Run(async () => {
                        var url = await homulilyClient.UploadArtAsync(bytes);
                        if (url != null) otherPlayerArt[key] = (url, otherPlayerArt.TryGetValue(key, out var a) ? a.songUrl : null);
                    });
                }
            } else if (otherPlayerArt.TryAdd(key, (null, null))) {
                var local = info.LocalArt;
                if (local != null) artUploaded[key] = true;
                _ = Task.Run(async () => {
                    try {
                        // 1. the cover the player itself has (uploaded to Homulily, since Discord needs a web address)
                        string? cover = local != null ? await homulilyClient.UploadArtAsync(local) : null;
                        // 2. Homulily's own lookup: Last.fm, then Deezer and iTunes
                        cover ??= await homulilyClient.GetCoverAsync(info.SongArtist, info.SongAlbum);
                        otherPlayerArt[key] = (cover, null);
                        // 3. Apple Music's site, for the song link (and the cover when nothing else had one)
                        var web = new AppleMusicWebScraper(info.SongName, info.SongAlbum, info.SongArtist, region, logger);
                        cover ??= await web.GetAlbumArtUrl();
                        otherPlayerArt[key] = (cover, await web.GetSongUrl());
                    } catch (Exception ex) {
                        logger?.Log($"[Presence] Cover lookup failed for {info.SongArtist} - {info.SongName}: {ex.Message}");
                    }
                });
                if (otherPlayerArt.Count > 500) { otherPlayerArt.Clear(); artUploaded.Clear(); }
            }
            if (info.CurrentTime is int position) {
                info.PlaybackStart = DateTime.UtcNow.AddSeconds(-position);
                if (info.SongDuration is int length) info.PlaybackEnd = info.PlaybackStart.Value.AddSeconds(length);
            }
        }

        private void UpdateOtherPlayerPresence(System.Collections.Generic.Dictionary<string, AppleMusicInfo?> bySource) {
            var settings = AMWin_RichPresence.Properties.Settings.Default;
            if (!settings.EnableDiscordRP || appleMusicActive) {
                if (!appleMusicActive) otherDiscordClient.Disable();
                return;
            }
            var pick = PresenceOrder.Where(OtherPlayerEnabled)
                .Select(s => bySource.TryGetValue(s, out var i) ? i : null)
                .FirstOrDefault(i => i != null && !i.IsPaused);
            if (pick == null) {
                otherDiscordClient.Disable();
                return;
            }
            otherDiscordClient.Enable();
            otherDiscordClient.SetPresence(pick, showSmallImage: false, settings.EnableRPCoverImages, settings.ShowAlbumTitle,
                                           fallbackImage: Constants.HomulilyPresenceImage, appleMusicIcons: false);
        }

        internal static bool OtherPlayerEnabled(string source) => source switch {
            "spotify" => AMWin_RichPresence.Properties.Settings.Default.HomulilySpotify,
            "musicbee" => AMWin_RichPresence.Properties.Settings.Default.HomulilyMusicBee,
            "foobar" => AMWin_RichPresence.Properties.Settings.Default.HomulilyFoobar,
            "browser" => AMWin_RichPresence.Properties.Settings.Default.HomulilyBrowsers,
            _ => AMWin_RichPresence.Properties.Settings.Default.HomulilyOtherPlayers,
        };

        internal async Task<bool> UpdateHomulilyCreds() {
            foreach (var client in otherPlayerClients.Values) {
                _ = client.UpdateCredsAsync(homulilyCredentials);
            }
            return await homulilyClient.UpdateCredsAsync(homulilyCredentials);
        }

        internal async Task<bool> UpdateListenBrainzCreds() {
            return await listenBrainzScrobblerClient.UpdateCredsAsync(listenBrainzCredentials);
        }

        internal void UpdateRegion() {
            var region = AMWin_RichPresence.Properties.Settings.Default.AppleMusicRegion;
            logger?.Log($"Changed region to {region}");
            amScraper.ChangeRegion(region);
        }

        internal void UpdateScraperPreferences(bool composerAsArtist) {
            amScraper.composerAsArtist = composerAsArtist;
        }

        /// <param name="quiet">The 6-hourly check: never pops a dialog, only shows the bar (or updates itself).</param>
        internal async Task CheckForUpdates(bool quiet = false) {
            try {
                var release = await Updater.CheckAsync();
                if (release == null) {
                    logger?.Log("No Homulily Desktop updates available.");
                    return;
                }
                AvailableUpdate = release;
                if (AMWin_RichPresence.Properties.Settings.Default.AutoUpdate) {
                    logger?.Log($"Updating to {release.Version} by itself");
                    var failed = await Updater.InstallAsync(release, null, logger);  // returns only when it failed
                    logger?.Log($"Automatic update failed: {failed}");
                }
                Application.Current.Dispatcher.Invoke(() => UpdateFound?.Invoke());
                if (quiet) return;
                Application.Current.Dispatcher.Invoke((Action)async delegate {
                    var answer = await new MessageBox {
                        Title = "Update available",
                        Content = $"Homulily Desktop {release.Version} is out. Update now? It takes a few seconds and restarts the app.",
                        IsCloseButtonEnabled = false,
                        PrimaryButtonText = "Update now",
                        SecondaryButtonText = "Later"
                    }.ShowDialogAsync();
                    if (answer == MessageBoxResult.Primary) await InstallUpdateOrOpenPage(release);
                });
            } catch (Exception e) {
                logger?.Log($"Could not check for Homulily Desktop updates: {e.Message}");
            }
        }

        internal async Task InstallUpdateOrOpenPage(Updater.Release release, IProgress<int>? progress = null) {
            var problem = await Updater.InstallAsync(release, progress, logger);
            if (problem != "") {
                await new MessageBox { Title = "Update failed", Content = problem + " Opening the download page instead.", CloseButtonText = "OK" }.ShowDialogAsync();
                Process.Start(new ProcessStartInfo { FileName = Constants.GithubReleasesUrl, UseShellExecute = true });
            }
        }
    }
}
