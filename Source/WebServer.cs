using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace MMCLIServerMod
{
    public class WebServer
    {
        private readonly HttpListener _listener;
        private readonly ConcurrentQueue<HttpListenerContext> _requestQueue;
        private readonly Thread _listenerThread;
        private readonly ManualLogSource _log;
        private readonly int _port;
        private volatile bool _running;

        public WebServer(int port, ManualLogSource log)
        {
            _port = port;
            _log = log;
            _requestQueue = new ConcurrentQueue<HttpListenerContext>();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            _listenerThread = new Thread(ListenerLoop)
            {
                IsBackground = true,
                Name = "MMCLIServerMod-HTTP"
            };
        }

        public void Start()
        {
            _running = true;
            try
            {
                _listener.Start();
            }
            catch (System.Exception ex)
            {
                _log.LogError($"HttpListener.Start() failed: {ex}");
                _running = false;
                return;
            }
            _listenerThread.Start();
            _log.LogInfo($"HTTP API listening on http://127.0.0.1:{_port}/");
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private void ListenerLoop()
        {
            while (_running)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    _requestQueue.Enqueue(ctx);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                        _log.LogError($"HTTP listener error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Called from Unity main thread in Update(). Processes queued HTTP requests
        /// so we can safely access Unity/Valheim APIs.
        /// </summary>
        public void ProcessPending()
        {
            while (_requestQueue.TryDequeue(out var ctx))
            {
                try
                {
                    HandleRequest(ctx);
                }
                catch (Exception ex)
                {
                    _log.LogError($"Error handling {ctx.Request.Url}: {ex.Message}");
                    try { Respond(ctx, 500, "{\"error\":\"internal server error\"}"); }
                    catch { }
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');

            if (ctx.Request.HttpMethod != "GET")
            {
                Respond(ctx, 405, "{\"error\":\"method not allowed\"}");
                return;
            }

            switch (path)
            {
                case "" or "/":
                    HandleHealth(ctx);
                    break;
                case "/plugins":
                    HandlePlugins(ctx);
                    break;
                case "/players":
                    HandlePlayers(ctx);
                    break;
                case "/status":
                    HandleStatus(ctx);
                    break;
                case "/events":
                    HandleEvents(ctx);
                    break;
                default:
                    Respond(ctx, 404, "{\"error\":\"not found\"}");
                    break;
            }
        }

        // GET / — health check
        private void HandleHealth(HttpListenerContext ctx)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Field("ok", true);
            w.Field("mod", MMCLIServerModPlugin.ModName);
            w.Field("version", MMCLIServerModPlugin.ModVersion);
            w.EndObject();
            Respond(ctx, 200, w.ToString());
        }

        // GET /plugins — all loaded BepInEx plugins
        private void HandlePlugins(HttpListenerContext ctx)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Key("plugins");
            w.BeginArray();

            foreach (var kvp in Chainloader.PluginInfos)
            {
                var meta = kvp.Value.Metadata;
                w.BeginObject();
                w.Field("guid", meta.GUID);
                w.Field("name", meta.Name);
                w.Field("version", meta.Version.ToString());

                var deps = kvp.Value.Dependencies;
                if (deps != null)
                {
                    w.Key("dependencies");
                    w.BeginArray();
                    foreach (var dep in deps)
                        w.Value(dep.DependencyGUID);
                    w.EndArray();
                }

                w.EndObject();
            }

            w.EndArray();
            w.EndObject();
            Respond(ctx, 200, w.ToString());
        }

        // GET /players — connected players
        private void HandlePlayers(HttpListenerContext ctx)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Key("players");
            w.BeginArray();

            if (ZNet.instance != null)
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (!peer.IsReady()) continue;
                    w.BeginObject();
                    w.Field("name", peer.m_playerName);
                    w.Field("host", peer.m_socket.GetHostName());
                    w.Field("uid", peer.m_uid);
                    w.Field("character_id", peer.m_characterID.ToString());
                    w.EndObject();
                }
            }

            w.EndArray();
            w.EndObject();
            Respond(ctx, 200, w.ToString());
        }

        // GET /status — server state
        private void HandleStatus(HttpListenerContext ctx)
        {
            var w = new JsonWriter();
            w.BeginObject();

            if (ZNet.instance != null)
            {
                w.Field("server_running", true);

                var world = ZNet.GetWorldIfIsHost();
                if (world != null)
                    w.Field("world", world.m_name);

                w.Field("is_dedicated", ZNet.instance.IsDedicated());
                w.Field("player_count", ZNet.instance.GetPeers().Count);

                if (EnvMan.instance != null)
                {
                    w.Field("day", EnvMan.instance.GetDay(ZNet.instance.GetTimeSeconds()));

                    float fraction = EnvMan.instance.GetDayFraction();
                    float totalHours = fraction * 24f;
                    int hour = (int)totalHours;
                    int minute = (int)((totalHours - hour) * 60f);
                    w.Field("game_time", $"{hour:D2}:{minute:D2}");
                    w.Field("is_day", EnvMan.IsDay());
                    w.Field("world_loaded", true);
                }

                w.Field("save_count", MMCLIServerModPlugin.SaveCount);
                if (MMCLIServerModPlugin.LastSave != null)
                    w.Field("last_save", MMCLIServerModPlugin.LastSave);
            }
            else
            {
                w.Field("server_running", false);
            }

            w.EndObject();
            Respond(ctx, 200, w.ToString());
        }

        // GET /events?after=N — game events since sequence N
        private void HandleEvents(HttpListenerContext ctx)
        {
            int after = 0;
            var afterParam = ctx.Request.QueryString["after"];
            if (afterParam != null)
                int.TryParse(afterParam, out after);

            var events = EventLog.GetAfter(after);
            var w = new JsonWriter();
            w.BeginObject();
            w.Key("events");
            w.BeginArray();
            foreach (var e in events)
            {
                w.BeginObject();
                w.Field("seq", e.Seq);
                w.Field("type", e.Type);
                w.Field("player", e.Player);
                if (e.Uid != 0)
                    w.Field("uid", e.Uid);
                w.Field("time", e.Time);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
            Respond(ctx, 200, w.ToString());
        }

        // --- HTTP response ---

        private static void Respond(HttpListenerContext ctx, int statusCode, string json)
        {
            var resp = ctx.Response;
            resp.StatusCode = statusCode;
            resp.ContentType = "application/json";
            var buf = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf, 0, buf.Length);
            resp.Close();
        }
    }

    /// <summary>
    /// Minimal JSON writer that tracks comma placement automatically.
    /// Avoids the error-prone "bool first" pattern in every handler.
    /// </summary>
    internal struct JsonWriter
    {
        private StringBuilder _sb;
        // Tracks whether the current object/array needs a comma before the next element.
        // Each bit represents a nesting level; set = needs comma.
        private int _commaFlags;
        private int _depth;

        private StringBuilder SB => _sb ??= new StringBuilder();

        public void BeginObject()
        {
            AppendComma();
            SB.Append('{');
            _depth++;
            ClearComma(); // fresh scope — no comma before first element
        }

        public void EndObject()
        {
            _depth--;
            ClearComma();
            SB.Append('}');
            SetComma(); // next sibling at parent level needs a comma
        }

        public void BeginArray()
        {
            SB.Append('[');
            _depth++;
            ClearComma(); // fresh scope — no comma before first element
        }

        public void EndArray()
        {
            _depth--;
            ClearComma();
            SB.Append(']');
            SetComma();
        }

        public void Key(string name)
        {
            AppendComma();
            SB.Append('"').Append(name).Append("\":");
        }

        public void Field(string name, string? value)
        {
            AppendComma();
            SB.Append('"').Append(name).Append("\":").Append(Escape(value));
        }

        public void Field(string name, bool value)
        {
            AppendComma();
            SB.Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
        }

        public void Field(string name, int value)
        {
            AppendComma();
            SB.Append('"').Append(name).Append("\":").Append(value);
        }

        public void Field(string name, long value)
        {
            AppendComma();
            SB.Append('"').Append(name).Append("\":").Append(value);
        }

        public void Value(string? value)
        {
            AppendComma();
            SB.Append(Escape(value));
        }

        public override string ToString() => SB.ToString();

        private void AppendComma()
        {
            if ((_commaFlags & (1 << _depth)) != 0)
                SB.Append(',');
            SetComma();
        }

        private void SetComma() => _commaFlags |= (1 << _depth);
        private void ClearComma() => _commaFlags &= ~(1 << _depth);

        private static string Escape(string? s)
        {
            if (s == null) return "null";
            return "\"" + s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t") + "\"";
        }
    }
}
