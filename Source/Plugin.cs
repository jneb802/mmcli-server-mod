using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace MMCLIServerMod
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class MMCLIServerModPlugin : BaseUnityPlugin
    {
        internal const string ModName = "MMCLIServerMod";
        internal const string ModVersion = "1.0.2";
        internal const string Author = "warpalicious";
        internal const string ModGUID = Author + "." + ModName;

        private readonly Harmony _harmony = new(ModGUID);

        internal static ManualLogSource Log = null!;
        internal static WebServer? Server;
        internal static int Port;
        internal static int SaveCount;
        internal static string? LastSave;

        public void Awake()
        {
            Log = Logger;

            Port = Config.Bind("HTTP", "Port", 9878,
                "Port for the local HTTP API. Only accessible from localhost.").Value;

            // Start HTTP server immediately — handlers gracefully handle ZNet being null
            try
            {
                Server = new WebServer(Port, Log);
                Server.Start();
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to start HTTP server: {ex}");
            }

            // Track world save events
            ZNet.WorldSaveFinished += () =>
            {
                SaveCount++;
                LastSave = DateTime.UtcNow.ToString("o");
                Log.LogInfo($"World save #{SaveCount} completed");
            };

            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.LogInfo($"{ModName} v{ModVersion} loaded — HTTP API on port {Port}");
        }

        public void Update()
        {
            Server?.ProcessPending();
        }

        public void OnDestroy()
        {
            Server?.Stop();
            _harmony.UnpatchSelf();
        }
    }
}
