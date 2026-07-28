// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes.Services;

public static class DeezerCdpService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient HttpClient = new();
    private const int DebugPort = 9222;

    public static string GetDeezerExePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string win32Path = Path.Combine(localAppData, "Programs", "deezer-desktop", "Deezer.exe");
        if (File.Exists(win32Path))
            return win32Path;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pfPath = Path.Combine(programFiles, "Deezer", "Deezer.exe");
        if (File.Exists(pfPath))
            return pfPath;

        return win32Path; // Default fallback
    }

    public static async Task<bool> IsCdpAvailableAsync()
    {
        try
        {
            var resp = await HttpClient.GetAsync($"http://localhost:{DebugPort}/json");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCdpAvailableSync
    {
        get
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(300) };
                var resp = client.GetAsync($"http://localhost:{DebugPort}/json").Result;
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task EnsureDeezerRunningWithDebugPortAsync()
    {
        if (await IsCdpAvailableAsync())
            return;

        try
        {
            var processes = Process.GetProcessesByName("Deezer");
            // If Deezer is ALREADY running, NEVER kill it!
            if (processes.Length > 0)
            {
                Logger.Info("Deezer is running without debug port. Skipping restart to avoid closing player.");
                return;
            }

            string exePath = GetDeezerExePath();
            if (!File.Exists(exePath))
            {
                Logger.Warn($"Deezer executable not found at {exePath}");
                return;
            }

            // Start Deezer with --remote-debugging-port=9222
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--remote-debugging-port={DebugPort}",
                UseShellExecute = true
            };
            Process.Start(psi);

            // Wait up to 3 seconds for CDP port to open
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(200);
                if (await IsCdpAvailableAsync())
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to launch Deezer with remote debugging port");
        }
    }

    public static async Task<bool> ExecuteJsAsync(string jsExpression)
    {
        await EnsureDeezerRunningWithDebugPortAsync();

        try
        {
            string json = await HttpClient.GetStringAsync($"http://localhost:{DebugPort}/json");
            using var doc = JsonDocument.Parse(json);
            
            string? wsUrl = null;
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                string url = elem.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                string type = elem.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                // Pick the main player window index.html (skip titlebar.html and background workers)
                if (type == "page" && url.Contains("index.html"))
                {
                    if (elem.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                    {
                        wsUrl = wsProp.GetString();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(wsUrl))
            {
                Logger.Warn("Could not find Deezer index.html WebSocket URL from CDP /json");
                return false;
            }

            using var ws = new ClientWebSocket();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            var payload = new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = jsExpression,
                    awaitPromise = true
                }
            };

            string requestJson = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(requestJson);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            using var ms = new MemoryStream();
            byte[] buffer = new byte[16384];
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string responseJson = Encoding.UTF8.GetString(ms.ToArray());

            Logger.Info($"CDP Evaluation result: {responseJson}");
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error executing JS expression via CDP");
            return false;
        }
    }

    public static async Task<string?> EvaluateJsAndReturnStringAsync(string jsExpression)
    {
        if (!await IsCdpAvailableAsync())
            return null;

        try
        {
            string json = await HttpClient.GetStringAsync($"http://localhost:{DebugPort}/json");
            using var doc = JsonDocument.Parse(json);
            
            string? wsUrl = null;
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                string url = elem.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                string type = elem.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type == "page" && url.Contains("index.html"))
                {
                    if (elem.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                    {
                        wsUrl = wsProp.GetString();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(wsUrl))
                return null;

            using var ws = new ClientWebSocket();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            var payload = new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = jsExpression,
                    awaitPromise = true,
                    returnByValue = true
                }
            };

            string requestJson = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(requestJson);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            using var ms = new MemoryStream();
            byte[] buffer = new byte[16384];
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string responseJson = Encoding.UTF8.GetString(ms.ToArray());

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);

            using var resDoc = JsonDocument.Parse(responseJson);
            if (resDoc.RootElement.TryGetProperty("result", out var resObj) &&
                resObj.TryGetProperty("result", out var innerRes) &&
                innerRes.TryGetProperty("value", out var valProp))
            {
                if (valProp.ValueKind == JsonValueKind.String)
                    return valProp.GetString();
                return valProp.GetRawText();
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error evaluating JS expression returning string");
            return null;
        }
    }

    public static async Task<bool> RemoveTrackAsync(int index)
    {
        if (index < 0) return false;
        string script = $"window.dzPlayer && typeof window.dzPlayer.removeTracks === 'function' ? (window.dzPlayer.removeTracks({index}), true) : false";
        return await ExecuteJsAsync(script);
    }

    public static async Task<bool> MoveTrackAsync(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex) return false;
        string script = $@"(() => {{
            if (!window.dzPlayer || typeof window.dzPlayer.getTrackList !== 'function') return false;
            let tracks = window.dzPlayer.getTrackList();
            if (!tracks || {fromIndex} < 0 || {fromIndex} >= tracks.length || {toIndex} < 0 || {toIndex} >= tracks.length) return false;
            let item = tracks.splice({fromIndex}, 1)[0];
            tracks.splice({toIndex}, 0, item);
            if (typeof window.dzPlayer.setTrackList === 'function') {{
                window.dzPlayer.setTrackList(tracks);
                return true;
            }}
            return false;
        }})()";
        return await ExecuteJsAsync(script);
    }

    private static int GetIntSafe(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number) return p.GetInt32();
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out int val)) return val;
        }
        return 0;
    }

    private static string GetStringSafe(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.String) return p.GetString() ?? "";
            return p.ToString() ?? "";
        }
        return "";
    }

    private static bool GetBoolSafe(JsonElement elem, string propName)
    {
        if (elem.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.True) return true;
            if (p.ValueKind == JsonValueKind.False) return false;
            if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out bool b)) return b;
        }
        return false;
    }

    public static async Task<List<DeezerTrack>?> GetQueueFromCdpAsync()
    {
        string js = """
        (function() {
            try {
                if (!window.dzPlayer || typeof window.dzPlayer.getTrackList !== 'function') return null;
                const list = window.dzPlayer.getTrackList();
                const curIdx = typeof window.dzPlayer.getIndexSong === 'function' ? window.dzPlayer.getIndexSong() : 0;
                return JSON.stringify({
                    curIndex: Number(curIdx) || 0,
                    tracks: list.map((t, idx) => ({
                        idx: Number(idx),
                        id: String(t.SNG_ID || ''),
                        title: String(t.SNG_TITLE || ''),
                        artist: String(t.ART_NAME || ''),
                        album: String(t.ALB_TITLE || ''),
                        coverPic: String(t.ALB_PICTURE || ''),
                        duration: parseInt(t.DURATION, 10) || 0,
                        isCurrent: Number(idx) === Number(curIdx)
                    }))
                });
            } catch(e) {
                return null;
            }
        })()
        """;

        string? resultJson = await EvaluateJsAndReturnStringAsync(js);
        if (string.IsNullOrEmpty(resultJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tracks", out var tracksArr)) return null;

            int curIdx = GetIntSafe(root, "curIndex");

            var tracks = new List<DeezerTrack>();
            foreach (var item in tracksArr.EnumerateArray())
            {
                int idx = GetIntSafe(item, "idx");
                string title = GetStringSafe(item, "title");
                string artist = GetStringSafe(item, "artist");
                string album = GetStringSafe(item, "album");
                string coverPic = GetStringSafe(item, "coverPic");
                int duration = GetIntSafe(item, "duration");
                bool isCurrent = GetBoolSafe(item, "isCurrent");

                string coverUrl = string.IsNullOrEmpty(coverPic)
                    ? ""
                    : $"https://e-cdns-images.dzcdn.net/images/cover/{coverPic}/250x250-000000-80-0-0.jpg";

                tracks.Add(new DeezerTrack
                {
                    Id = GetStringSafe(item, "id"),
                    Title = title,
                    Artist = artist,
                    Album = album,
                    CoverUrl = coverUrl,
                    DurationSeconds = duration,
                    TargetIndex = idx,
                    IsCurrent = isCurrent,
                    SkipCount = idx - curIdx
                });
            }

            return tracks;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error parsing CDP tracklist");
            return null;
        }
    }

    public static async Task<bool> PlayTrackAtIndexAsync(int targetIndex)
    {
        string js = $"(function() {{ if (window.dzPlayer && typeof window.dzPlayer.playTrackAtIndex === 'function') {{ window.dzPlayer.playTrackAtIndex({targetIndex}); return 'played_' + {targetIndex}; }} return false; }})()";
        return await ExecuteJsAsync(js);
    }

    /// <summary>
    /// Jump forward by skipCount tracks in Deezer Web Player via CDP JS execution.
    /// </summary>
    public static async Task<bool> SkipTracksAsync(int skipCount)
    {
        string js = $$"""
        (function() {
            try {
                if (window.dzPlayer) {
                    if (typeof window.dzPlayer.playTrackAtIndex === 'function') {
                        const curIdx = typeof window.dzPlayer.getIndexSong === 'function' ? window.dzPlayer.getIndexSong() : 0;
                        const targetIdx = curIdx + {{skipCount}};
                        window.dzPlayer.playTrackAtIndex(targetIdx);
                        return 'played_at_index_' + targetIdx;
                    }
                    if (window.dzPlayer.control && typeof window.dzPlayer.control.next === 'function') {
                        for (let i = 0; i < {{skipCount}}; i++) {
                            window.dzPlayer.control.next();
                        }
                        return 'called_next';
                    }
                }
                // Fallback: click Next button in Deezer DOM
                for (let i = 0; i < {{skipCount}}; i++) {
                    const btn = document.querySelector('button[aria-label="Morceau suivant"], button[aria-label="Next track"], button[aria-label="Next"]');
                    if (btn) btn.click();
                }
                return 'dom_clicked';
            } catch(e) {
                return 'error: ' + e.message;
            }
        })()
        """;

        return await ExecuteJsAsync(js);
    }
}
