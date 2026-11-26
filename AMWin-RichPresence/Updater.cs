using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace AMWin_RichPresence {
    /// <summary>
    /// One-button updates from the GitHub releases (Homulily Desktop, 2026-11-26). The new exe is downloaded next to the running
    /// one and checked against the SHA-256 GitHub publishes for it; then the running exe is renamed aside (Windows allows renaming
    /// a running program, not replacing it), the new one takes its name, it is started, and this copy exits. The renamed copy is
    /// deleted on the next start. Any failure puts the old exe back.
    /// </summary>
    internal static class Updater {
        public record Release(string Version, string DownloadUrl, string Sha256, long Size);

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient() {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("HomulilyDesktop-Updater");
            return http;
        }

        private static string ExePath => Constants.ExePath ?? throw new InvalidOperationException("Cannot find my own exe.");
        private static string OldPath => ExePath + ".old";
        private static string DownloadPath => ExePath + ".download";

        public static int VersionNumber(string v) {
            var digits = v.TrimStart('v', 'V').Split('b', '-')[0].Replace(".", "").PadRight(4, '0');
            return int.TryParse(digits, out var n) ? n : 0;
        }

        /// <summary>The newest release when it is newer than this copy, else null.</summary>
        public static async Task<Release?> CheckAsync() {
            var json = await Http.GetStringAsync(Constants.GithubReleasesApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = root.GetProperty("tag_name").GetString() ?? "";
            if (VersionNumber(version) <= VersionNumber(Constants.ProgramVersionBase)) return null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray()) {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                if (!digest.StartsWith("sha256:")) continue;  // nothing to check the download against: not installed automatically
                return new Release(version, asset.GetProperty("browser_download_url").GetString()!, digest[7..].ToLowerInvariant(),
                                   asset.GetProperty("size").GetInt64());
            }
            return null;
        }

        /// <summary>Downloads, checks and swaps in the release, then starts it. Returns only on failure (with the reason).</summary>
        public static async Task<string> InstallAsync(Release release, IProgress<int>? progress, Logger? logger) {
            try {
                using (var resp = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead)) {
                    resp.EnsureSuccessStatusCode();
                    await using var input = await resp.Content.ReadAsStreamAsync();
                    await using var output = File.Create(DownloadPath);
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0) {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                        done += read;
                        if (release.Size > 0) progress?.Report((int)(done * 100 / release.Size));
                    }
                }
                string actual;
                await using (var file = File.OpenRead(DownloadPath)) {
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
                }
                if (actual != release.Sha256) {
                    File.Delete(DownloadPath);
                    return "The download did not match GitHub's checksum, so I did not install it.";
                }
                if (File.Exists(OldPath)) File.Delete(OldPath);
                File.Move(ExePath, OldPath);
                try {
                    File.Move(DownloadPath, ExePath);
                } catch {
                    File.Move(OldPath, ExePath);  // put the old exe back
                    throw;
                }
                logger?.Log($"[Updater] Installed {release.Version}, restarting");
                Process.Start(new ProcessStartInfo { FileName = ExePath, UseShellExecute = true });
                System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
                return "";
            } catch (Exception ex) {
                logger?.Log($"[Updater] Update failed: {ex}");
                try { if (File.Exists(DownloadPath)) File.Delete(DownloadPath); } catch { }
                return ex is UnauthorizedAccessException
                    ? "I cannot write to the folder I am in. Move me somewhere like Documents, or download the update by hand."
                    : $"The update failed: {ex.Message}";
            }
        }

        /// <summary>Removes the copy an update left behind. Called on every start.</summary>
        public static void CleanUp() {
            try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { }
            try { if (File.Exists(DownloadPath)) File.Delete(DownloadPath); } catch { }
        }
    }
}
