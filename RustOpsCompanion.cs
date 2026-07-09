using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Carbon;
using Carbon.Plugins;
using Oxide.Plugins;
using Oxide.Game.Rust.Cui;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Carbon.Plugins;

[Info("RustOpsCompanion", "RustOps", "0.5.3")]
[Description("Secure outbound companion for the RustOps hosted control plane.")]
public class RustOpsCompanion : CarbonPlugin
{
    private const int ProtocolVersion = 1;
    private const string CompanionVersion = "0.5.3";
    private const string CompanionBuild = "2026.07.09.4";
    private const int MaxConfigBytes = 2 * 1024 * 1024;
    private readonly CancellationTokenSource shutdown = new();
    private ClientWebSocket socket;
    private int connectionGeneration;
    private CompanionSettings settings;
    private string pendingPairingCode;
    private System.Threading.Timer updateTimer;
    private readonly Dictionary<ulong, PendingWarning> pendingWarnings = new();
    private static readonly string ConfigRoot = Path.Combine("carbon", "configs");
    private string SettingsPath => Path.Combine(ConfigRoot, "RustOpsCompanion.json");

    private sealed class CompanionSettings
    {
        public string ServiceUrl = "wss://your-rustops-domain/v1/carbon/connect";
        public string DeviceToken = "";
        public bool AutoUpdate = false;
    }

    private sealed class PluginSummary
    {
        public string id;
        public string name;
        public string author;
        public string version;
        public string state;
        public string description;
        public bool hasConfig;
    }

    private sealed class ProtocolMessage
    {
        [JsonProperty("protocolVersion")] public int ProtocolVersion = RustOpsCompanion.ProtocolVersion;
        [JsonProperty("requestId")] public string RequestId;
        [JsonProperty("operation")] public string Operation;
        [JsonProperty("capabilities", NullValueHandling = NullValueHandling.Ignore)] public string[] Capabilities;
        [JsonProperty("success", NullValueHandling = NullValueHandling.Ignore)] public bool? Success;
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public object Error;
        [JsonProperty("payload", NullValueHandling = NullValueHandling.Ignore)] public JToken Payload;
    }

    private sealed class ReleaseManifest
    {
        public string version;
        public string sourceUrl;
        public string sha256;
    }

    private sealed class PendingWarning { public string Token; public string WarningId; }

    private void OnServerInitialized()
    {
        LoadSettings();
        if (!string.IsNullOrWhiteSpace(settings.DeviceToken)) StartConnectionLoop();
        ConfigureUpdateTimer();
    }

    private void Unload()
    {
        shutdown.Cancel();
        updateTimer?.Dispose();
        foreach (var player in BasePlayer.activePlayerList) CuiHelper.DestroyUi(player, WarningUi(player.userID));
        try { socket?.Abort(); socket?.Dispose(); } catch { }
    }

    [ConsoleCommand("rustops.pair")]
    private void Pair(ConsoleSystem.Arg arg)
    {
        if (arg.Connection != null && arg.Connection.authLevel < 2) { arg.ReplyWith("Admin level 2 required."); return; }
        if (arg.Args == null || arg.Args.Length < 1) { arg.ReplyWith("Usage: rustops.pair <six-digit-code> [wss://service/v1/carbon/connect]"); return; }
        if (arg.Args.Length > 1)
        {
            if (!Uri.TryCreate(arg.Args[1].ToString(), UriKind.Absolute, out var uri) || !IsAllowedServiceUri(uri)) { arg.ReplyWith("Service URL must use wss:// (ws:// allowed only for private LAN testing)."); return; }
            settings.ServiceUrl = uri.ToString();
        }
        pendingPairingCode = arg.Args[0].ToString(); settings.DeviceToken = ""; SaveSettings();
        StartConnectionLoop();
        arg.ReplyWith("RustOps pairing started.");
    }

    [ConsoleCommand("rustops.status")]
    private void Status(ConsoleSystem.Arg arg) => arg.ReplyWith(
        $"RustOps Companion v{CompanionVersion} ({CompanionBuild})\n" +
        $"Protocol: v{ProtocolVersion}\n" +
        $"Paired: {!string.IsNullOrWhiteSpace(settings?.DeviceToken)}\n" +
        $"Connection: {socket?.State.ToString() ?? "Not started"}\n" +
        $"Service: {settings?.ServiceUrl ?? "Not configured"}\n" +
        $"Auto update: {(settings?.AutoUpdate == true ? "enabled" : "disabled")}\n" +
        "Capabilities: plugins.list, plugins.lifecycle, config.read, config.write, config.rollback, player.warn, chat.send, companion.update, companion.autoupdate");

    [ConsoleCommand("rustops.autoupdate")]
    private void AutoUpdate(ConsoleSystem.Arg arg)
    {
        if (arg.Connection != null && arg.Connection.authLevel < 2) { arg.ReplyWith("Admin level 2 required."); return; }
        if (arg.Args == null || arg.Args.Length < 1) { arg.ReplyWith($"Auto update is {(settings.AutoUpdate ? "enabled" : "disabled")}. Usage: rustops.autoupdate true|false"); return; }
        if (!bool.TryParse(arg.Args[0].ToString(), out var enabled)) { arg.ReplyWith("Usage: rustops.autoupdate true|false"); return; }
        settings.AutoUpdate = enabled; SaveSettings(); ConfigureUpdateTimer();
        arg.ReplyWith($"RustOps automatic updates {(enabled ? "enabled" : "disabled")}. Updates are verified with SHA-256.");
        if (enabled) _ = CheckForUpdate();
    }

    [ConsoleCommand("rustops.version")]
    private void Version(ConsoleSystem.Arg arg) => arg.ReplyWith($"RustOpsCompanion {CompanionVersion} build {CompanionBuild}; protocol v{ProtocolVersion}.");

    [ConsoleCommand("rustops.update")]
    private void ManualUpdate(ConsoleSystem.Arg arg)
    {
        if (arg.Connection != null && arg.Connection.authLevel < 2) { arg.ReplyWith("Admin level 2 required."); return; }
        arg.ReplyWith("RustOps update check started. A newer signed release will be installed and reloaded automatically.");
        _ = CheckForUpdate();
    }

    [ConsoleCommand("rustops.changelog")]
    private void Changelog(ConsoleSystem.Arg arg) => arg.ReplyWith(
        "v0.5.3: Prevent duplicate WebSocket receive loops after pairing/reconnect.\n" +
        "v0.5.2: Manual rustops.update command and dashboard update trigger.\n" +
        "v0.5.1: WebSocket-pushed update availability and immediate verified auto-update.\n" +
        "v0.5.0: Custom dashboard chat sender without the SERVER prefix.\n" +
        "v0.4.1: Fixed acknowledgement button compatibility on Carbon builds without ConsoleSystem.Arg.Player().\n" +
        "v0.4.0: Acknowledgement warning popup; warning delivery/ack protocol; chat reliability release.\n" +
        "v0.3.1: Fixed Carbon Timer type ambiguity.\n" +
        "v0.3.0: Canonical plugin IDs; quoted c.load/c.unload/c.reload; config detection/errors; opt-in SHA-256 auto-update.\n" +
        "v0.2.1: Removed unavailable internal ModLoader dependency; filesystem-backed plugin inventory.\n" +
        "v0.2.0: Carbon production compatibility; detailed pairing/status; companion version handshake; legacy runtime file/hash support.\n" +
        "v0.1.0: Initial pairing, plugin lifecycle, and JSON configuration bridge.");

    private void StartConnectionLoop()
    {
        var generation = Interlocked.Increment(ref connectionGeneration);
        try { socket?.Abort(); } catch { }
        _ = ConnectionLoop(generation);
    }

    private async Task ConnectionLoop(int generation)
    {
        var attempt = 0;
        while (!shutdown.IsCancellationRequested && generation == connectionGeneration)
        {
            try
            {
                socket?.Dispose(); socket = new ClientWebSocket();
                if (!string.IsNullOrEmpty(pendingPairingCode)) socket.Options.SetRequestHeader("X-Pairing-Code", pendingPairingCode);
                else socket.Options.SetRequestHeader("Authorization", $"Bearer {settings.DeviceToken}");
                await socket.ConnectAsync(new Uri(settings.ServiceUrl), shutdown.Token);
                if (generation != connectionGeneration) return;
                attempt = 0; Puts("Connected to RustOps control plane.");
                await Send(new ProtocolMessage { RequestId = Guid.NewGuid().ToString(), Operation = "hello", Capabilities = new[] { "plugins.list", "plugins.lifecycle", "config.read", "config.write", "config.rollback", "player.warn", "chat.send", "companion.update", "companion.status", "companion.autoupdate" }, Payload = JObject.FromObject(new { carbonVersion = typeof(CarbonPlugin).Assembly.GetName().Version?.ToString() ?? "unknown", companionVersion = CompanionVersion, companionBuild = CompanionBuild, autoUpdate = settings.AutoUpdate }) });
                await ReceiveLoop(generation, socket);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (generation != connectionGeneration) { return; }
            catch (Exception error) { PrintWarning($"Connection lost: {error.Message}"); }
            if (!shutdown.IsCancellationRequested && generation == connectionGeneration) await Task.Delay(Math.Min(30000, 1000 * (1 << Math.Min(attempt++, 5))), shutdown.Token);
        }
    }

    private static bool IsAllowedServiceUri(Uri uri)
    {
        if (uri.Scheme == "wss") return true;
        if (uri.Scheme != "ws") return false;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(uri.Host, out var ip)) return false;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31));
    }

    private async Task ReceiveLoop(int generation, ClientWebSocket activeSocket)
    {
        var buffer = new byte[64 * 1024];
        while (activeSocket.State == WebSocketState.Open && !shutdown.IsCancellationRequested && generation == connectionGeneration)
        {
            using var stream = new MemoryStream(); WebSocketReceiveResult result;
            do
            {
                result = await activeSocket.ReceiveAsync(new ArraySegment<byte>(buffer), shutdown.Token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > MaxConfigBytes + 65536) throw new InvalidDataException("Protocol message too large.");
            } while (!result.EndOfMessage);
            var message = JsonConvert.DeserializeObject<ProtocolMessage>(Encoding.UTF8.GetString(stream.ToArray()));
            if (message == null || message.ProtocolVersion != ProtocolVersion) continue;
            if (message.Operation == "paired")
            {
                settings.DeviceToken = message.Payload?["token"]?.Value<string>() ?? ""; pendingPairingCode = null; SaveSettings(); Puts("Pairing complete; device credential saved."); continue;
            }
            _ = Handle(message);
        }
    }

    private async Task Handle(ProtocolMessage request)
    {
        try
        {
            object payload = request.Operation switch
            {
                "plugins.list" => await OnMainThread(ListPlugins),
                "plugins.load" => await RunLifecycle("load", request),
                "plugins.unload" => await RunLifecycle("unload", request),
                "plugins.reload" => await RunLifecycle("reload", request),
                "config.read" => ReadConfig(PluginName(request)),
                "config.write" => WriteConfig(PluginName(request), request),
                "config.rollback" => RollbackConfig(PluginName(request)),
                "player.warn" => await OnMainThread(() => WarnPlayer(request)),
                "chat.send" => await OnMainThread(() => SendChat(request)),
                "companion.update" => QueueUpdate(request),
                _ => throw new InvalidOperationException("Unsupported operation.")
            };
            await Send(new ProtocolMessage { RequestId = request.RequestId, Operation = request.Operation, Success = true, Payload = payload == null ? null : JToken.FromObject(payload) });
        }
        catch (Exception error)
        {
            await Send(new ProtocolMessage { RequestId = request.RequestId, Operation = request.Operation, Success = false, Error = new { code = "operation_failed", message = error.Message } });
        }
    }

    private object ListPlugins()
    {
        var root = Path.GetFullPath(Path.Combine("carbon", "plugins"));
        if (!Directory.Exists(root)) return new { plugins = Array.Empty<PluginSummary>() };
        var plugins = new List<PluginSummary>();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(file);
            var info = Regex.Match(source, "\\[Info\\s*\\(\\s*\\\"([^\\\"]+)\\\"\\s*,\\s*\\\"([^\\\"]+)\\\"\\s*,\\s*\\\"([^\\\"]+)\\\"\\s*\\)\\]");
            var description = Regex.Match(source, "\\[Description\\s*\\(\\s*\\\"([^\\\"]*)\\\"\\s*\\)\\]");
            plugins.Add(new PluginSummary {
                id = Path.GetFileNameWithoutExtension(file),
                name = info.Success ? info.Groups[1].Value : Path.GetFileNameWithoutExtension(file),
                author = info.Success ? info.Groups[2].Value : "Unknown",
                version = info.Success ? info.Groups[3].Value : "Unknown",
                state = "installed",
                description = description.Success ? description.Groups[1].Value : "",
                hasConfig = File.Exists(ResolveConfigPath(Path.GetFileNameWithoutExtension(file)))
            });
        }
        return new { plugins = plugins.OrderBy(plugin => plugin.name).ToArray() };
    }

    private async Task<object> RunLifecycle(string action, ProtocolMessage request)
    {
        var plugin = PluginName(request);
        return await OnMainThread(() => new { output = ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), $"c.{action} \"{plugin}\"") });
    }

    private string PluginName(ProtocolMessage request)
    {
        var value = request.Payload?["plugin"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Plugin name required.");
        var safe = Path.GetFileNameWithoutExtension(value);
        if (!string.Equals(value, safe, StringComparison.Ordinal) || value.Contains("\"")) throw new InvalidDataException("Invalid plugin ID.");
        return safe;
    }

    private string ConfigPath(string plugin)
    {
        var root = Path.GetFullPath(ConfigRoot) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, plugin + ".json"));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Config path escaped root.");
        return path;
    }

    private string ResolveConfigPath(string plugin)
    {
        var exact = ConfigPath(plugin); if (File.Exists(exact) || !Directory.Exists(ConfigRoot)) return exact;
        return Directory.GetFiles(ConfigRoot, "*.json", SearchOption.TopDirectoryOnly).FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), plugin, StringComparison.OrdinalIgnoreCase)) ?? exact;
    }

    private object ReadConfig(string plugin)
    {
        var path = ResolveConfigPath(plugin); if (!File.Exists(path)) throw new FileNotFoundException($"No JSON config exists for plugin '{plugin}'.");
        var bytes = File.ReadAllBytes(path); if (bytes.Length > MaxConfigBytes) throw new InvalidDataException("Config exceeds 2 MiB.");
        var text = Encoding.UTF8.GetString(bytes); return new { content = JToken.Parse(text), revision = Revision(bytes) };
    }

    private object WriteConfig(string plugin, ProtocolMessage request)
    {
        var path = ResolveConfigPath(plugin); if (!File.Exists(path)) throw new FileNotFoundException($"No JSON config exists for plugin '{plugin}'.");
        var current = File.ReadAllBytes(path); var expected = request.Payload?["revision"]?.Value<string>();
        if (!string.Equals(expected, Revision(current), StringComparison.Ordinal)) throw new InvalidOperationException("Config changed since it was opened.");
        var content = request.Payload?["content"] ?? throw new InvalidDataException("Config content required.");
        var bytes = Encoding.UTF8.GetBytes(content.ToString(Formatting.Indented)); if (bytes.Length > MaxConfigBytes) throw new InvalidDataException("Config exceeds 2 MiB.");
        JToken.Parse(Encoding.UTF8.GetString(bytes)); Backup(plugin, current);
        var temporary = path + ".rustops.tmp"; File.WriteAllBytes(temporary, bytes); File.Replace(temporary, path, null);
        return new { revision = Revision(bytes) };
    }

    private object RollbackConfig(string plugin)
    {
        var path = ResolveConfigPath(plugin); var folder = BackupFolder(plugin);
        var backup = Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(file => file.CreationTimeUtc).FirstOrDefault() : null;
        if (backup == null) throw new FileNotFoundException("No config backup available.");
        var current = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>(); var bytes = File.ReadAllBytes(backup.FullName);
        if (current.Length > 0) Backup(plugin, current); File.WriteAllBytes(path, bytes); return new { revision = Revision(bytes) };
    }

    private object WarnPlayer(ProtocolMessage request)
    {
        var steamId = request.Payload?["steamId"]?.Value<string>(); var message = request.Payload?["message"]?.Value<string>(); var warningId = request.Payload?["warningId"]?.Value<string>();
        if (!ulong.TryParse(steamId, out var id) || steamId.Length != 17) throw new InvalidDataException("Valid Steam ID required.");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 512) throw new InvalidDataException("Warning message required (maximum 512 characters).");
        if (string.IsNullOrWhiteSpace(warningId)) throw new InvalidDataException("Warning ID required.");
        var player = BasePlayer.FindByID(id); if (player == null || !player.IsConnected) throw new InvalidOperationException("Player is not online.");
        var token = Guid.NewGuid().ToString("N"); pendingWarnings[id] = new PendingWarning { Token = token, WarningId = warningId };
        ShowWarning(player, message, token); return new { delivered = true, steamId, warningId };
    }

    private object SendChat(ProtocolMessage request)
    {
        var name = request.Payload?["name"]?.Value<string>()?.Trim().TrimEnd(':');
        var message = request.Payload?["message"]?.Value<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32) throw new InvalidDataException("Chat name required (maximum 32 characters).");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 512) throw new InvalidDataException("Chat message required (maximum 512 characters).");
        foreach (var player in BasePlayer.activePlayerList)
            player.SendConsoleCommand("chat.add", 2, 0, message, name, "#b8d9ff", 1.0);
        Puts($"[RustOps chat] {name}: {message}");
        return new { delivered = true, name };
    }

    private object QueueUpdate(ProtocolMessage request)
    {
        var available = request.Payload?["availableVersion"]?.Value<string>() ?? "unknown";
        var force = request.Payload?["force"]?.Value<bool>() == true;
        if (!settings.AutoUpdate && !force) return new { accepted = false, reason = "automatic_updates_disabled", availableVersion = available };
        Puts($"RustOps control plane announced companion v{available}; starting verified update.");
        _ = CheckForUpdate();
        return new { accepted = true, manual = force, availableVersion = available };
    }

    private static string WarningUi(ulong userId) => "RustOps.Warning." + userId;
    private void ShowWarning(BasePlayer player, string message, string token)
    {
        var name = WarningUi(player.userID); CuiHelper.DestroyUi(player, name); var elements = new CuiElementContainer();
        var overlay = elements.Add(new CuiPanel { Image = { Color = "0 0 0 0.78" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, CursorEnabled = true }, "Overlay", name);
        var panel = elements.Add(new CuiPanel { Image = { Color = "0.08 0.09 0.09 0.98" }, RectTransform = { AnchorMin = "0.31 0.28", AnchorMax = "0.69 0.72" } }, overlay);
        elements.Add(new CuiLabel { Text = { Text = "ADMIN WARNING", FontSize = 24, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.75 1 0.15 1" }, RectTransform = { AnchorMin = "0.08 0.75", AnchorMax = "0.92 0.94" } }, panel);
        elements.Add(new CuiLabel { Text = { Text = message.Replace("<", "‹").Replace(">", "›"), FontSize = 18, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.95 0.96 0.95 1" }, RectTransform = { AnchorMin = "0.08 0.30", AnchorMax = "0.92 0.74" } }, panel);
        elements.Add(new CuiButton { Button = { Color = "0.55 0.78 0.08 1", Command = "rustops.warn.ack " + player.userID + " " + token }, RectTransform = { AnchorMin = "0.25 0.08", AnchorMax = "0.75 0.24" }, Text = { Text = "I READ THE NOTE", FontSize = 16, Align = UnityEngine.TextAnchor.MiddleCenter, Color = "0.05 0.06 0.04 1" } }, panel);
        CuiHelper.AddUi(player, elements);
    }

    [ConsoleCommand("rustops.warn.ack")]
    private void AcknowledgeWarning(ConsoleSystem.Arg arg)
    {
        if (arg.Args == null || arg.Args.Length < 2 || !ulong.TryParse(arg.Args[0].ToString(), out var steamId)) return;
        if (!pendingWarnings.TryGetValue(steamId, out var warning) || !string.Equals(warning.Token, arg.Args[1].ToString(), StringComparison.Ordinal)) return;
        pendingWarnings.Remove(steamId);
        var player = BasePlayer.FindByID(steamId); if (player != null) CuiHelper.DestroyUi(player, WarningUi(steamId));
        _ = Send(new ProtocolMessage { RequestId = Guid.NewGuid().ToString(), Operation = "player.warn.acknowledged", Success = true, Payload = JObject.FromObject(new { warningId = warning.WarningId, steamId = steamId.ToString() }) });
    }

    private string BackupFolder(string plugin) => Path.Combine(ConfigRoot, ".rustops-backups", plugin);
    private void Backup(string plugin, byte[] bytes)
    {
        var folder = BackupFolder(plugin); Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json"), bytes);
        foreach (var old in new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(file => file.CreationTimeUtc).Skip(5)) old.Delete();
    }
    private static string Revision(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

    private void ConfigureUpdateTimer()
    {
        updateTimer?.Dispose(); updateTimer = null;
        if (settings?.AutoUpdate == true) updateTimer = new System.Threading.Timer(state => { var ignored = CheckForUpdate(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
    }

    private async Task CheckForUpdate()
    {
        try
        {
            var service = new Uri(settings.ServiceUrl);
            var scheme = service.Scheme == "wss" ? "https" : "http";
            var origin = new UriBuilder(scheme, service.Host, service.IsDefaultPort ? -1 : service.Port).Uri;
            using var web = new WebClient();
            var manifestUri = new Uri(origin, "/downloads/release.json");
            var manifest = JsonConvert.DeserializeObject<ReleaseManifest>(await web.DownloadStringTaskAsync(manifestUri));
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.version) || string.IsNullOrWhiteSpace(manifest.sourceUrl) || string.IsNullOrWhiteSpace(manifest.sha256)) throw new InvalidDataException("Invalid update manifest.");
            if (!System.Version.TryParse(manifest.version, out var available) || !System.Version.TryParse(CompanionVersion, out var installed)) throw new InvalidDataException("Invalid release version.");
            if (available <= installed) { Puts($"RustOpsCompanion v{CompanionVersion} is already up to date."); return; }
            Uri sourceUri;
            if (Uri.TryCreate(manifest.sourceUrl, UriKind.Absolute, out var absolute) && (absolute.Scheme == "http" || absolute.Scheme == "https")) sourceUri = absolute;
            else sourceUri = new Uri(origin, manifest.sourceUrl);
            if (sourceUri.Scheme != scheme || !string.Equals(sourceUri.Host, service.Host, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update source must use the configured service origin.");
            var bytes = await web.DownloadDataTaskAsync(sourceUri);
            if (!string.Equals(Revision(bytes), manifest.sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update SHA-256 verification failed.");
            var target = Path.GetFullPath(Path.Combine("carbon", "plugins", "RustOpsCompanion.cs"));
            var temporary = target + ".rustops-update"; File.WriteAllBytes(temporary, bytes);
            try { File.Replace(temporary, target, target + ".previous"); }
            catch { File.Copy(temporary, target, true); File.Delete(temporary); }
            Puts($"RustOpsCompanion updated to v{manifest.version}; Carbon will compile/reload it.");
        }
        catch (Exception error) { PrintWarning($"Automatic update check failed: {error.Message}"); }
    }

    private Task<T> OnMainThread<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(); NextTick(() => { try { result.SetResult(action()); } catch (Exception error) { result.SetException(error); } }); return result.Task;
    }

    private async Task Send(ProtocolMessage message)
    {
        if (socket?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, shutdown.Token);
    }

    private void LoadSettings()
    {
        try { settings = File.Exists(SettingsPath) ? JsonConvert.DeserializeObject<CompanionSettings>(File.ReadAllText(SettingsPath)) : new CompanionSettings(); }
        catch { settings = new CompanionSettings(); }
        SaveSettings();
    }
    private void SaveSettings() { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented)); }
}
