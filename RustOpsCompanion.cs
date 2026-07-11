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

[Info("RustOpsCompanion", "RustOps", "0.6.3")]
[Description("Secure outbound companion for the RustOps hosted control plane.")]
public class RustOpsCompanion : CarbonPlugin
{
    private const int ProtocolVersion = 1;
    private const string CompanionVersion = "0.6.3";
    private const string CompanionBuild = "2026.07.12.1";
    private const int MaxConfigBytes = 2 * 1024 * 1024;
    private const int StableConnectionSeconds = 30;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private ClientWebSocket socket;
    private int connectionGeneration;
    private int consecutiveConnectionFailures;
    private DateTime pausedUntilUtc = DateTime.MinValue;
    private DateTime nextRetryUtc = DateTime.MinValue;
    private string lastConnectionError = "";
    private DateTime lastConnectedLogUtc = DateTime.MinValue;
    private CompanionSettings settings;
    private string pendingPairingCode;
    private System.Threading.Timer updateTimer;
    private readonly Dictionary<ulong, PendingWarning> pendingWarnings = new();
    private readonly Dictionary<string, string> compileErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> loadedPlugins = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string ConfigRoot = Path.Combine("carbon", "configs");
    private string SettingsPath => Path.Combine(ConfigRoot, "RustOpsCompanion.json");

    private sealed class CompanionSettings
    {
        public string ServiceUrl = "wss://your-rustops-domain/v1/carbon/connect";
        public string DeviceToken = "";
        public bool AutoUpdate = false;
        public string UpdateChannel = "stable";
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
        public string compileError;
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
        RefreshLoadedPluginsFromRuntime();
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

    private void OnPluginCompileFailure(string plugin, Exception error)
    {
        if (string.IsNullOrWhiteSpace(plugin)) return;
        compileErrors[Path.GetFileNameWithoutExtension(plugin)] = error?.Message ?? "Compilation failed.";
        loadedPlugins.Remove(Path.GetFileNameWithoutExtension(plugin));
    }

    private void OnPluginLoaded(object plugin)
    {
        var name = PluginObjectName(plugin); if (string.IsNullOrWhiteSpace(name)) return;
        compileErrors.Remove(name); loadedPlugins.Add(name);
    }

    private void OnPluginUnloaded(object plugin)
    {
        var name = PluginObjectName(plugin); if (!string.IsNullOrWhiteSpace(name)) loadedPlugins.Remove(name);
    }

    private static string PluginObjectName(object plugin)
    {
        try { return plugin?.GetType().GetProperty("Name")?.GetValue(plugin, null)?.ToString(); }
        catch { return null; }
    }

    private void RefreshLoadedPluginsFromRuntime()
    {
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException error) { types = error.Types.Where(type => type != null).ToArray(); }
                catch { continue; }
                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || (!typeof(CarbonPlugin).IsAssignableFrom(type) && !InheritsTypeNamed(type, "RustPlugin"))) continue;
                    AddLoadedPluginName(type.Name);
                    foreach (var attribute in type.GetCustomAttributes(false))
                    {
                        var attributeType = attribute.GetType();
                        if (!string.Equals(attributeType.Name, "InfoAttribute", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var propertyName in new[] { "Name", "Title", "PluginName" })
                            AddLoadedPluginName(attributeType.GetProperty(propertyName)?.GetValue(attribute, null)?.ToString());
                    }
                }
            }
        }
        catch { }
    }

    private void AddLoadedPluginName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        loadedPlugins.Add(Path.GetFileNameWithoutExtension(name));
    }

    private static bool InheritsTypeNamed(Type type, string name)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
            if (string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
        $"Connection: {ConnectionLabel()}\n" +
        $"Next retry: {RetryLabel()}\n" +
        $"Last error: {(string.IsNullOrWhiteSpace(lastConnectionError) ? "none" : lastConnectionError)}\n" +
        $"Service: {settings?.ServiceUrl ?? "Not configured"}\n" +
        $"Auto update: {(settings?.AutoUpdate == true ? "enabled" : "disabled")}\n" +
        $"Update channel: {settings?.UpdateChannel ?? "stable"}\n" +
        "Capabilities: plugins.list, plugins.lifecycle, config.read, config.write, config.rollback, player.warn, chat.send, companion.update, companion.status, companion.autoupdate, companion.retry, companion.channel");

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

    [ConsoleCommand("rustops.retry")]
    private void Retry(ConsoleSystem.Arg arg)
    {
        if (arg.Connection != null && arg.Connection.authLevel < 2) { arg.ReplyWith("Admin level 2 required."); return; }
        consecutiveConnectionFailures = 0;
        pausedUntilUtc = DateTime.MinValue;
        nextRetryUtc = DateTime.MinValue;
        lastConnectionError = "";
        StartConnectionLoop();
        arg.ReplyWith("RustOps connection retry forced.");
    }

    [ConsoleCommand("rustops.changelog")]
    private void Changelog(ConsoleSystem.Arg arg) => arg.ReplyWith(
        "v0.6.3: Per-server stable/beta update channels with signed channel manifests.\n" +
        "v0.6.2: Treats stable proxy/server WebSocket closes as normal reconnects instead of noisy last-error failures.\n" +
        "v0.6.1: Detects already-loaded plugins on startup and fixes update manifest compatibility.\n" +
        "v0.6.0: Strict protocol handling, serialized WebSocket sends, remote status/update controls, and atomic config rollback.\n" +
        "v0.5.5: Short-lived WebSocket connections now count as failures; connection logs are throttled.\n" +
        "v0.5.4: Smarter WebSocket reconnect backoff, pause-after-failures, rustops.retry command, and clearer status.\n" +
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
        pausedUntilUtc = DateTime.MinValue;
        nextRetryUtc = DateTime.MinValue;
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
                if (pausedUntilUtc > DateTime.UtcNow)
                {
                    nextRetryUtc = pausedUntilUtc;
                    await Task.Delay(TimeUntil(pausedUntilUtc), shutdown.Token);
                    continue;
                }
                socket?.Dispose(); socket = new ClientWebSocket();
                if (!string.IsNullOrEmpty(pendingPairingCode)) socket.Options.SetRequestHeader("X-Pairing-Code", pendingPairingCode);
                else socket.Options.SetRequestHeader("Authorization", $"Bearer {settings.DeviceToken}");
                await socket.ConnectAsync(new Uri(settings.ServiceUrl), shutdown.Token);
                if (generation != connectionGeneration) return;
                var connectedAtUtc = DateTime.UtcNow;
                pausedUntilUtc = DateTime.MinValue; nextRetryUtc = DateTime.MinValue;
                AnnounceConnected();
                await Send(new ProtocolMessage { RequestId = Guid.NewGuid().ToString(), Operation = "hello", Capabilities = new[] { "plugins.list", "plugins.lifecycle", "config.read", "config.write", "config.rollback", "player.warn", "chat.send", "companion.update", "companion.status", "companion.autoupdate", "companion.retry", "companion.channel" }, Payload = JObject.FromObject(new { carbonVersion = typeof(CarbonPlugin).Assembly.GetName().Version?.ToString() ?? "unknown", companionVersion = CompanionVersion, companionBuild = CompanionBuild, autoUpdate = settings.AutoUpdate, updateChannel = settings.UpdateChannel }) });
                try { await ReceiveLoop(generation, socket); }
                catch (WebSocketException error) when (IsUncleanRemoteClose(error))
                {
                    var seconds = (DateTime.UtcNow - connectedAtUtc).TotalSeconds;
                    if (seconds < StableConnectionSeconds) throw new WebSocketException($"WebSocket closed uncleanly after {Math.Max(1, (int)seconds)} second(s).");
                }
                if (generation != connectionGeneration || shutdown.IsCancellationRequested) return;
                var connectedSeconds = (DateTime.UtcNow - connectedAtUtc).TotalSeconds;
                if (connectedSeconds < StableConnectionSeconds)
                    HandleConnectionFailure(new WebSocketException($"WebSocket closed after {Math.Max(1, (int)connectedSeconds)} second(s)."));
                else
                {
                    attempt = 0;
                    consecutiveConnectionFailures = 0;
                    lastConnectionError = "";
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
            catch (OperationCanceledException) when (generation != connectionGeneration) { return; }
            catch (Exception error) { HandleConnectionFailure(error); }
            if (!shutdown.IsCancellationRequested && generation == connectionGeneration)
            {
                var delay = RetryDelay(attempt++);
                nextRetryUtc = DateTime.UtcNow.Add(delay);
                await Task.Delay(delay, shutdown.Token);
            }
        }
    }

    private void HandleConnectionFailure(Exception error)
    {
        lastConnectionError = error.Message;
        consecutiveConnectionFailures++;
        if (consecutiveConnectionFailures >= 6)
        {
            var minutes = Math.Min(30, 5 * (1 + ((consecutiveConnectionFailures - 6) / 3)));
            pausedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
            PrintWarning($"Connection lost: {error.Message}. Pausing retries for {minutes} minute(s). Run rustops.retry to force retry.");
            return;
        }
        if (consecutiveConnectionFailures == 1 || consecutiveConnectionFailures == 3)
            PrintWarning($"Connection lost: {error.Message}. Retrying with backoff.");
    }

    private void AnnounceConnected()
    {
        if (DateTime.UtcNow - lastConnectedLogUtc < TimeSpan.FromMinutes(1)) return;
        lastConnectedLogUtc = DateTime.UtcNow;
        Puts("Connected to RustOps control plane.");
    }

    private TimeSpan RetryDelay(int attempt)
    {
        if (pausedUntilUtc > DateTime.UtcNow) return TimeUntil(pausedUntilUtc);
        var seconds = Math.Min(60, 2 * (1 << Math.Min(attempt, 5)));
        return TimeSpan.FromSeconds(seconds + UnityEngine.Random.Range(0, 4));
    }

    private static TimeSpan TimeUntil(DateTime utc) => TimeSpan.FromMilliseconds(Math.Max(1000, (utc - DateTime.UtcNow).TotalMilliseconds));
    private string ConnectionLabel() => pausedUntilUtc > DateTime.UtcNow ? "Paused" : socket?.State.ToString() ?? "Not started";
    private string RetryLabel() => nextRetryUtc > DateTime.UtcNow ? nextRetryUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "not scheduled";
    private static bool IsUncleanRemoteClose(WebSocketException error) => error.Message.IndexOf("without completing the close handshake", StringComparison.OrdinalIgnoreCase) >= 0;

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
            if (message == null || string.IsNullOrWhiteSpace(message.RequestId) || string.IsNullOrWhiteSpace(message.Operation))
                throw new InvalidDataException("Malformed RustOps protocol message.");
            if (message.ProtocolVersion != ProtocolVersion)
            {
                lastConnectionError = $"Unsupported protocol v{message.ProtocolVersion}; this companion requires v{ProtocolVersion}.";
                await activeSocket.CloseAsync(WebSocketCloseStatus.ProtocolError, lastConnectionError, shutdown.Token);
                return;
            }
            if (message.Operation == "paired")
            {
                var token = message.Payload?["token"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("Pairing response did not contain a device credential.");
                settings.DeviceToken = token; pendingPairingCode = null; SaveSettings(); Puts("Pairing complete; device credential saved."); continue;
            }
            await Handle(message);
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
                "companion.status" => CompanionStatus(),
                "companion.autoupdate" => SetAutoUpdate(request),
                "companion.retry" => QueueRetry(),
                "companion.channel" => SetUpdateChannel(request),
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
        RefreshLoadedPluginsFromRuntime();
        var root = Path.GetFullPath(Path.Combine("carbon", "plugins"));
        if (!Directory.Exists(root)) return new { plugins = Array.Empty<PluginSummary>() };
        var plugins = new List<PluginSummary>();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(file);
            var info = Regex.Match(source, "\\[Info\\s*\\(\\s*\\\"([^\\\"]+)\\\"\\s*,\\s*\\\"([^\\\"]+)\\\"\\s*,\\s*\\\"([^\\\"]+)\\\"\\s*\\)\\]");
            var description = Regex.Match(source, "\\[Description\\s*\\(\\s*\\\"([^\\\"]*)\\\"\\s*\\)\\]");
            var id = Path.GetFileNameWithoutExtension(file);
            compileErrors.TryGetValue(id, out var compileError);
            plugins.Add(new PluginSummary {
                id = id,
                name = info.Success ? info.Groups[1].Value : Path.GetFileNameWithoutExtension(file),
                author = info.Success ? info.Groups[2].Value : "Unknown",
                version = info.Success ? info.Groups[3].Value : "Unknown",
                state = !string.IsNullOrWhiteSpace(compileError) ? "compile_error" : loadedPlugins.Contains(id) ? "loaded" : "installed",
                description = description.Success ? description.Groups[1].Value : "",
                hasConfig = File.Exists(ResolveConfigPath(id)),
                compileError = compileError
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
        if (current.Length > 0) Backup(plugin, current);
        var temporary = path + ".rustops.tmp"; File.WriteAllBytes(temporary, bytes);
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        return new { revision = Revision(bytes) };
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

    private object CompanionStatus() => new
    {
        version = CompanionVersion,
        build = CompanionBuild,
        protocolVersion = ProtocolVersion,
        paired = !string.IsNullOrWhiteSpace(settings?.DeviceToken),
        connection = ConnectionLabel(),
        nextRetry = RetryLabel(),
        lastError = lastConnectionError,
        autoUpdate = settings?.AutoUpdate == true
        ,updateChannel = settings?.UpdateChannel ?? "stable"
    };

    private object SetUpdateChannel(ProtocolMessage request)
    {
        var channel = request.Payload?["channel"]?.Value<string>()?.ToLowerInvariant();
        if (channel != "stable" && channel != "beta") throw new InvalidDataException("Update channel must be stable or beta.");
        settings.UpdateChannel = channel; SaveSettings();
        if (settings.AutoUpdate) _ = CheckForUpdate();
        return new { channel };
    }

    private object SetAutoUpdate(ProtocolMessage request)
    {
        var enabled = request.Payload?["enabled"]?.Value<bool?>() ?? throw new InvalidDataException("enabled boolean required.");
        settings.AutoUpdate = enabled; SaveSettings(); ConfigureUpdateTimer();
        if (enabled) _ = CheckForUpdate();
        return new { enabled };
    }

    private object QueueRetry()
    {
        consecutiveConnectionFailures = 0; pausedUntilUtc = DateTime.MinValue; nextRetryUtc = DateTime.MinValue; lastConnectionError = "";
        NextTick(StartConnectionLoop);
        return new { accepted = true };
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
            var channel = settings?.UpdateChannel == "beta" ? "beta" : "stable";
            var manifestUri = new Uri(origin, "/v1/carbon/releases/" + channel);
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
        await sendLock.WaitAsync(shutdown.Token);
        try
        {
            if (socket?.State == WebSocketState.Open)
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, shutdown.Token);
        }
        finally { sendLock.Release(); }
    }

    private void LoadSettings()
    {
        try { settings = File.Exists(SettingsPath) ? JsonConvert.DeserializeObject<CompanionSettings>(File.ReadAllText(SettingsPath)) : new CompanionSettings(); }
        catch { settings = new CompanionSettings(); }
        SaveSettings();
    }
    private void SaveSettings() { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented)); }
}
