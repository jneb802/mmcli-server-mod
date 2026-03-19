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
        internal const string ModVersion = "1.0.0";
        internal const string Author = "warpalicious";
        internal const string ModGUID = Author + "." + ModName;

        private readonly Harmony _harmony = new(ModGUID);

        internal static ManualLogSource Log = null!;
        internal static WebServer? Server;

        public void Awake()
        {
            Log = Logger;

            var port = Config.Bind("HTTP", "Port", 9878,
                "Port for the local HTTP API. Only accessible from localhost.");

            Server = new WebServer(port.Value, Log);
            Server.Start();

            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.LogInfo($"{ModName} v{ModVersion} loaded — HTTP API on port {port.Value}");
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
