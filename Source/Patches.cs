using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MMCLIServerMod
{
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    static class PeerInfoPatch
    {
        static void Postfix(ZRpc rpc)
        {
            if (!ZNet.m_isServer) return;
            var peer = ZNet.instance?.GetPeer(rpc);
            if (peer == null || string.IsNullOrEmpty(peer.m_playerName)) return;
            EventLog.Add("player_joined", peer.m_playerName, peer.m_uid);
        }
    }

    [HarmonyPatch(typeof(ZNet), "Disconnect", typeof(ZNetPeer))]
    static class DisconnectPatch
    {
        static void Prefix(ZNetPeer peer)
        {
            if (!ZNet.m_isServer) return;
            if (peer == null || string.IsNullOrEmpty(peer.m_playerName)) return;
            EventLog.Add("player_left", peer.m_playerName, peer.m_uid);
        }
    }

    [HarmonyPatch(typeof(Player), "OnDeath")]
    static class PlayerDeathPatch
    {
        // On a dedicated server the Postfix can fire multiple frames before the
        // client's s_dead ZDO update arrives to stop CheckDeath. Deduplicate.
        private static readonly Dictionary<int, float> _lastDeath = new();

        static void Postfix(Player __instance)
        {
            if (!ZNet.m_isServer) return;

            var id = __instance.GetInstanceID();
            var now = Time.time;
            if (_lastDeath.TryGetValue(id, out var last) && now - last < 10f)
                return;
            _lastDeath[id] = now;

            var name = __instance.GetPlayerName();
            if (string.IsNullOrEmpty(name)) return;
            EventLog.Add("player_died", name);
        }
    }
}
