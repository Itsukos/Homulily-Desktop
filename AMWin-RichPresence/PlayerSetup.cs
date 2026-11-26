using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace AMWin_RichPresence {
    /// <summary>
    /// One-click setup of the add-ons MusicBee and foobar2000 need before they tell Windows what they play (Homulily Desktop,
    /// 2026-11-26). Each add-on is downloaded from its author's own GitHub release and checked before use: foo_mediacontrol
    /// against the SHA-256 GitHub publishes, mb_MediaControl (whose release predates GitHub's checksums) against one pinned here.
    /// foobar2000 installs a component itself, so we hand it the file and it asks "install?"; MusicBee loads any DLL in the
    /// Plugins folder next to MusicBee.exe, so we copy it there (one Windows admin prompt when that folder is in Program Files)
    /// and restart MusicBee.
    /// </summary>
    internal static class PlayerSetup {
        public const string MusicBeePluginUrl = "https://github.com/HenryPDT/mb_MediaControl/releases/download/1.0/mb_MediaControl.dll";
        public const string MusicBeePluginSha256 = "bd9b6eb5649c2432c65438e4cd5c78ef1b620acdd3fca5ba69e2ff8770b8d5a9";
        public const string FoobarReleaseApi = "https://api.github.com/repos/ungive/foo_mediacontrol/releases/latest";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient() {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("HomulilyDesktop-PlayerSetup");
            return http;
        }

        public enum State { NotInstalled, Basic, Full }

        // --- finding the players -------------------------------------------------------------------------------------------

        private static string? RunningExe(string processName) {
            foreach (var p in Process.GetProcessesByName(processName)) {
                try { return p.MainModule?.FileName; } catch { }
            }
            return null;
        }

        private static IEnumerable<string> UninstallLocations(string displayName) {
            var roots = new[] { Registry.LocalMachine, Registry.CurrentUser };
            var paths = new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (var root in roots) {
                foreach (var path in paths) {
                    using var key = root.OpenSubKey(path);
                    if (key == null) continue;
                    foreach (var name in key.GetSubKeyNames()) {
                        using var sub = key.OpenSubKey(name);
                        var shown = sub?.GetValue("DisplayName") as string ?? "";
                        var location = sub?.GetValue("InstallLocation") as string;
                        if (shown.Contains(displayName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(location)) {
                            yield return location.Trim('"');
                        }
                    }
                }
            }
        }

        private static string? FindExe(string processName, string exeName, string displayName, string folderName) {
            var running = RunningExe(processName);
            if (running != null) return running;
            var candidates = UninstallLocations(displayName).ToList();
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), folderName));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), folderName));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", folderName));
            return candidates.Select(dir => Path.Combine(dir, exeName)).FirstOrDefault(File.Exists);
        }

        public static string? FindMusicBee() => FindExe("MusicBee", "MusicBee.exe", "MusicBee", "MusicBee");

        public static string? FindFoobar() => FindExe("foobar2000", "foobar2000.exe", "foobar2000", "foobar2000");

        // --- is the add-on there ------------------------------------------------------------------------------------------

        public static State MusicBeeState() {
            var exe = FindMusicBee();
            if (exe == null) return State.NotInstalled;
            return File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Plugins", "mb_MediaControl.dll")) ? State.Full : State.Basic;
        }

        public static State FoobarState() {
            var exe = FindFoobar();
            if (exe == null) return State.NotInstalled;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.GetDirectoryName(exe)!;
            var places = new[] {
                Path.Combine(appData, "foobar2000-v2", "user-components-x64", "foo_mediacontrol"),
                Path.Combine(appData, "foobar2000-v2", "user-components", "foo_mediacontrol"),
                Path.Combine(appData, "foobar2000", "user-components", "foo_mediacontrol"),
                Path.Combine(dir, "profile", "user-components-x64", "foo_mediacontrol"),
                Path.Combine(dir, "profile", "user-components", "foo_mediacontrol"),
            };
            return places.Any(Directory.Exists) ? State.Full : State.Basic;
        }

        // --- installing ---------------------------------------------------------------------------------------------------

        private static async Task<byte[]> DownloadChecked(string url, string sha256) {
            var bytes = await Http.GetByteArrayAsync(url);
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (actual != sha256.ToLowerInvariant()) {
                throw new InvalidOperationException("The download did not match its checksum, so I did not install it.");
            }
            return bytes;
        }

        /// <summary>Installs mb_MediaControl and restarts MusicBee. Returns what to tell the user.</summary>
        public static async Task<string> InstallMusicBeeAsync(Logger? logger) {
            var exe = FindMusicBee();
            if (exe == null) return "MusicBee is not installed on this PC.";
            try {
                var bytes = await DownloadChecked(MusicBeePluginUrl, MusicBeePluginSha256);
                var plugins = Path.Combine(Path.GetDirectoryName(exe)!, "Plugins");
                var target = Path.Combine(plugins, "mb_MediaControl.dll");
                var tmp = Path.Combine(Path.GetTempPath(), "mb_MediaControl.dll");
                await File.WriteAllBytesAsync(tmp, bytes);
                try {
                    Directory.CreateDirectory(plugins);
                    File.Copy(tmp, target, overwrite: true);
                } catch (UnauthorizedAccessException) {
                    // Program Files: one admin prompt to copy the checked file into place
                    var copy = Process.Start(new ProcessStartInfo {
                        FileName = "cmd.exe",
                        Arguments = $"/c mkdir \"{plugins}\" 2>nul & copy /y \"{tmp}\" \"{target}\"",
                        Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden
                    });
                    copy?.WaitForExit();
                }
                if (!File.Exists(target)) return "The plugin was not copied. Allow the Windows prompt and try again.";
                logger?.Log("[Player setup] Installed mb_MediaControl for MusicBee");
                var running = Process.GetProcessesByName("MusicBee");
                if (running.Length == 0) return "Done. MusicBee will report everything next time you open it.";
                foreach (var p in running) {
                    p.CloseMainWindow();
                    await Task.Run(() => p.WaitForExit(15000));
                }
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                return "Done. MusicBee restarted with the plugin.";
            } catch (System.ComponentModel.Win32Exception) {
                return "The Windows admin prompt was declined, so nothing was installed.";
            } catch (Exception ex) {
                logger?.Log($"[Player setup] MusicBee setup failed: {ex}");
                return $"Setup failed: {ex.Message}";
            }
        }

        private static bool Is64Bit(string exe) {
            using var f = File.OpenRead(exe);
            using var r = new BinaryReader(f);
            f.Seek(0x3C, SeekOrigin.Begin);
            f.Seek(r.ReadInt32() + 4, SeekOrigin.Begin);
            return r.ReadUInt16() == 0x8664;
        }

        /// <summary>Downloads foo_mediacontrol and hands it to foobar2000, which asks whether to install it.</summary>
        public static async Task<string> InstallFoobarAsync(Logger? logger) {
            var exe = FindFoobar();
            if (exe == null) return "foobar2000 is not installed on this PC.";
            try {
                var wanted = Is64Bit(exe) ? "-x64.fb2k-component" : "-x86.fb2k-component";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(FoobarReleaseApi));
                var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
                    .FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").EndsWith(wanted));
                if (asset.ValueKind != JsonValueKind.Object) return "I could not find the right component to download.";
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                if (!digest.StartsWith("sha256:")) return "GitHub gave no checksum for the component, so I did not install it.";
                var bytes = await DownloadChecked(asset.GetProperty("browser_download_url").GetString()!, digest[7..]);
                var file = Path.Combine(Path.GetTempPath(), asset.GetProperty("name").GetString()!);
                await File.WriteAllBytesAsync(file, bytes);
                Process.Start(new ProcessStartInfo { FileName = exe, Arguments = $"\"{file}\"", UseShellExecute = true });
                logger?.Log("[Player setup] Handed foo_mediacontrol to foobar2000");
                return "foobar2000 is asking to install it: click Yes, then let it restart.";
            } catch (Exception ex) {
                logger?.Log($"[Player setup] foobar2000 setup failed: {ex}");
                return $"Setup failed: {ex.Message}";
            }
        }
    }
}
