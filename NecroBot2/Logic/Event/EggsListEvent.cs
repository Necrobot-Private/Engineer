﻿#region using directives

using System.Collections.Generic;
using POGOProtos.Inventory;
using PoGo.NecroBot.Logic.Event;

#endregion

namespace NecroBot2.Logic.Event
{
    public class EggsListEvent : IEvent
    {
        public float PlayerKmWalked { get; set; }
        public List<EggIncubator> Incubators { get; set; }
        public object UnusedEggs { get; set; }
    }
}