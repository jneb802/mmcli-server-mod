using System;
using System.Collections.Generic;

namespace MMCLIServerMod
{
    internal struct GameEvent
    {
        public int Seq;
        public string Type;
        public string Player;
        public long Uid;
        public string Time;
    }

    internal static class EventLog
    {
        private static readonly List<GameEvent> _events = new();
        private static readonly object _lock = new();
        private static int _nextSeq = 1;
        private const int MaxEvents = 200;

        public static void Add(string type, string player, long uid = 0)
        {
            lock (_lock)
            {
                _events.Add(new GameEvent
                {
                    Seq = _nextSeq++,
                    Type = type,
                    Player = player,
                    Uid = uid,
                    Time = DateTime.UtcNow.ToString("o"),
                });

                if (_events.Count > MaxEvents)
                    _events.RemoveRange(0, _events.Count - MaxEvents);
            }
        }

        public static List<GameEvent> GetAfter(int afterSeq)
        {
            lock (_lock)
            {
                var result = new List<GameEvent>();
                foreach (var e in _events)
                {
                    if (e.Seq > afterSeq)
                        result.Add(e);
                }
                return result;
            }
        }
    }
}
