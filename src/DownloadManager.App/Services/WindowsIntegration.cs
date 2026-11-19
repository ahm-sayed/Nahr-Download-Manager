using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DownloadManager.Engine;
using Microsoft.Win32;

namespace DownloadManager.App.Services;

/// <summary>
/// Per-user Windows integration (HKCU only — no admin rights required):
/// 1) Auto-start Nahr silently in the system tray on Windows login (--minimized).
/// 2) Register the Chrome / Edge / Brave / Firefox Native Messaging Host so the browser extension
///    can auto-launch Nahr on demand even when Nahr is completely closed (just like IDM).
/// </summary>
public static partial class WindowsIntegration
{
    public const string NativeHostName = "com.nahr.downloader";
    public const string FirefoxExtensionId = "nahr@nahr.app";
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Nahr";

    private static readonly string[] ChromiumNativeHostRegistryPaths =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\" + NativeHostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + NativeHostName,
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + NativeHostName,
        @"Software\Chromium\NativeMessagingHosts\" + NativeHostName,
    ];

    private const string FirefoxNativeHostRegistryPath = @"Software\Mozilla\NativeMessagingHosts\" + NativeHostName;

    private static readonly object SyncGate = new();
    private static readonly HashSet<string> KnownOrigins = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-p]{32}$", RegexOptions.Compiled)]
    private static partial Regex ChromiumExtensionIdRegex();

    /// <summary>
    /// Detects whether this process was launched by Chrome/Edge/Brave/Firefox as a Native Messaging Host.
    /// Chrome/Edge/Brave pass <c>chrome-extension://&lt;id&gt;/</c> as argv[1]; Firefox passes the manifest JSON path.
    /// </summary>
    public static bool IsNativeMessagingHostLaunch(string[] args)
    {
        if (args is null || args.Length == 0) return false;
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (arg.Equals("--native-host", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                || arg.EndsWith(NativeHostName + ".json", StringComparison.OrdinalIgnoreCase)
                || arg.EndsWith(NativeHostName + ".firefox.json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Enables or disables automatic silent tray launch at Windows sign-in.</summary>
    public static void SyncStartWithWindows(bool enabled, string? exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (key is null) return;

            if (enabled && !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                key.SetValue(RunValueName, $"\"{exePath}\" --minimized", RegistryValueKind.String);
            }
            else if (!enabled)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Never fail app startup if registry access is restricted by policy.
        }
    }

    /// <summary>
    /// Writes per-user Native Messaging Host manifests under %LOCALAPPDATA%\Nahr\native-host and registers
    /// them in HKCU for Chrome, Edge, Brave, Chromium, and Firefox.
    /// </summary>
    public static void RegisterNativeMessagingHost(string? exePath, IEnumerable<string>? additionalOrigins = null)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;

        try
        {
            lock (SyncGate)
            {
                var hostDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Nahr",
                    "native-host");
                Directory.CreateDirectory(hostDir);

                var chromiumManifestPath = Path.Combine(hostDir, NativeHostName + ".json");
                var firefoxManifestPath = Path.Combine(hostDir, NativeHostName + ".firefox.json");

                var origins = new HashSet<string>(KnownOrigins, StringComparer.OrdinalIgnoreCase);
                LoadExistingAllowedOrigins(chromiumManifestPath, origins);
                DiscoverInstalledChromiumExtensionOrigins(origins);

                if (additionalOrigins is not null)
                {
                    foreach (var raw in additionalOrigins)
                    {
                        var normalized = NormalizeChromiumOrigin(raw);
                        if (normalized is not null) origins.Add(normalized);
                    }
                }

                // Avoid rewriting disk & registry if nothing changed after initial registration
                var hadAll = origins.Count > 0 && KnownOrigins.Count == origins.Count && origins.IsSubsetOf(KnownOrigins);
                if (hadAll && File.Exists(chromiumManifestPath) && File.Exists(firefoxManifestPath))
                    return;

                foreach (var o in origins) KnownOrigins.Add(o);

                var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

                // Write Chromium manifest (Chrome / Edge / Brave / Chromium)
                if (origins.Count > 0)
                {
                    var chromiumManifest = new
                    {
                        name = NativeHostName,
                        description = "Nahr Download Manager Native Messaging Host",
                        path = exePath,
                        type = "stdio",
                        allowed_origins = origins.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    };
                    File.WriteAllText(chromiumManifestPath, JsonSerializer.Serialize(chromiumManifest, jsonOpts), new UTF8Encoding(false));

                    foreach (var regPath in ChromiumNativeHostRegistryPaths)
                    {
                        try
                        {
                            using var key = Registry.CurrentUser.CreateSubKey(regPath, writable: true);
                            key?.SetValue(null, chromiumManifestPath, RegistryValueKind.String);
                        }
                        catch { }
                    }
                }

                // Write Firefox manifest
                var firefoxManifest = new
                {
                    name = NativeHostName,
                    description = "Nahr Download Manager Native Messaging Host",
                    path = exePath,
                    type = "stdio",
                    allowed_extensions = new[] { FirefoxExtensionId },
                };
                File.WriteAllText(firefoxManifestPath, JsonSerializer.Serialize(firefoxManifest, jsonOpts), new UTF8Encoding(false));

                try
                {
                    using var ffKey = Registry.CurrentUser.CreateSubKey(FirefoxNativeHostRegistryPath, writable: true);
                    ffKey?.SetValue(null, firefoxManifestPath, RegistryValueKind.String);
                }
                catch { }
            }
        }
        catch
        {
            // Best-effort integration; never crash the app.
        }
    }

    /// <summary>
    /// Runs the Chrome/Firefox Native Messaging stdio protocol:
    /// reads one 4-byte length-prefixed JSON message, ensures Nahr is running silently in tray (--minimized),
    /// waits until the local extension HTTP server responds on /ping, and replies with JSON on stdout.
    /// </summary>
    public static async Task RunNativeMessagingHostAsync(string[] args)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            var requestJson = await ReadNativeMessageAsync(stdin).ConfigureAwait(false);
            if (requestJson is null) return;

            var action = "ensureRunning";
            try
            {
                using var doc = JsonDocument.Parse(requestJson);
                if (doc.RootElement.TryGetProperty("action", out var actEl) && actEl.ValueKind == JsonValueKind.String)
                    action = actEl.GetString() ?? "ensureRunning";
            }
            catch { }

            var alreadyAlive = await IsExtensionServerAliveAsync().ConfigureAwait(false);
            var launched = false;

            if (!alreadyAlive && !string.Equals(action, "ping", StringComparison.OrdinalIgnoreCase))
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    // UseShellExecute = true ensures the spawned Nahr process does NOT inherit Chrome's stdio pipes.
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--minimized",
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                        UseShellExecute = true,
                    };
                    Process.Start(psi);
                    launched = true;

                    // Wait up to 8 seconds for Nahr's HTTP ExtensionServer to become ready
                    var deadline = Environment.TickCount64 + 8000;
                    while (Environment.TickCount64 < deadline)
                    {
                        await Task.Delay(150).ConfigureAwait(false);
                        if (await IsExtensionServerAliveAsync().ConfigureAwait(false))
                        {
                            alreadyAlive = true;
                            break;
                        }
                    }
                }
            }

            var response = JsonSerializer.Serialize(new
            {
                ok = alreadyAlive,
                status = alreadyAlive ? "running" : "timeout",
                launched,
                port = ExtensionServer.DefaultPort,
            });
            await WriteNativeMessageAsync(stdout, response).ConfigureAwait(false);
        }
        catch
        {
            // Native host must exit cleanly without popping up error dialogs to the browser.
        }
    }

    private static async Task<string?> ReadNativeMessageAsync(Stream stdin)
    {
        var lenBuf = new byte[4];
        var got = await ReadExactAsync(stdin, lenBuf).ConfigureAwait(false);
        if (got < 4) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (length <= 0 || length > 1024 * 1024) return null;

        var payload = new byte[length];
        var bodyGot = await ReadExactAsync(stdin, payload).ConfigureAwait(false);
        if (bodyGot < length) return null;

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task WriteNativeMessageAsync(Stream stdout, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBuf, payload.Length);
        await stdout.WriteAsync(lenBuf).ConfigureAwait(false);
        await stdout.WriteAsync(payload).ConfigureAwait(false);
        await stdout.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total)).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static async Task<bool> IsExtensionServerAliveAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{ExtensionServer.DefaultPort}/ping").ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeChromiumOrigin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (ChromiumExtensionIdRegex().IsMatch(s))
            return $"chrome-extension://{s.ToLowerInvariant()}/";

        if (s.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = s["chrome-extension://".Length..].TrimEnd('/');
            var slash = rest.IndexOf('/');
            if (slash >= 0) rest = rest[..slash];
            if (ChromiumExtensionIdRegex().IsMatch(rest))
                return $"chrome-extension://{rest.ToLowerInvariant()}/";
        }
        return null;
    }

    private static void LoadExistingAllowedOrigins(string manifestPath, HashSet<string> target)
    {
        if (!File.Exists(manifestPath)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("allowed_origins", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var norm = NormalizeChromiumOrigin(el.GetString());
                    if (norm is not null) target.Add(norm);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Scans local Chrome/Edge/Brave profile preferences (read-only) to find any already-loaded unpacked
    /// Nahr extension ID so Native Messaging works immediately without manual setup.
    /// </summary>
    private static void DiscoverInstalledChromiumExtensionOrigins(HashSet<string> target)
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] userDataRoots =
        [
            Path.Combine(localApp, "Google", "Chrome", "User Data"),
            Path.Combine(localApp, "Microsoft", "Edge", "User Data"),
            Path.Combine(localApp, "BraveSoftware", "Brave-Browser", "User Data"),
        ];

        foreach (var root in userDataRoots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> profileDirs;
            try { profileDirs = Directory.EnumerateDirectories(root); }
            catch { continue; }

            foreach (var profileDir in profileDirs)
            {
                foreach (var fileName in new[] { "Secure Preferences", "Preferences" })
                {
                    var prefPath = Path.Combine(profileDir, fileName);
                    if (!File.Exists(prefPath)) continue;
                    try
                    {
                        using var fs = new FileStream(prefPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var doc = JsonDocument.Parse(fs);
                        if (!doc.RootElement.TryGetProperty("extensions", out var extEl)
                            || !extEl.TryGetProperty("settings", out var settingsEl)
                            || settingsEl.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        foreach (var prop in settingsEl.EnumerateObject())
                        {
                            if (!ChromiumExtensionIdRegex().IsMatch(prop.Name)) continue;
                            var val = prop.Value;
                            var matched = false;
                            if (val.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
                            {
                                var p = pathEl.GetString() ?? "";
                                if (p.Contains("Nahr", StringComparison.OrdinalIgnoreCase)
                                    && p.Contains("browser-extension", StringComparison.OrdinalIgnoreCase))
                                {
                                    matched = true;
                                }
                            }
                            if (!matched
                                && val.TryGetProperty("manifest", out var manEl)
                                && manEl.TryGetProperty("name", out var nameEl)
                                && string.Equals(nameEl.GetString(), "Nahr Download Manager", StringComparison.OrdinalIgnoreCase))
                            {
                                matched = true;
                            }

                            if (matched)
                                target.Add($"chrome-extension://{prop.Name.ToLowerInvariant()}/");
                        }
                    }
                    catch { }
                }
            }
        }
    }
}
