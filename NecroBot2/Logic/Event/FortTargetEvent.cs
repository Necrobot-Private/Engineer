﻿using PoGo.NecroBot.Logic.Event;

namespace NecroBot2.Logic.Event
{
    public class FortTargetEvent : IEvent
    {
        public double Distance;
        public string Name;
    }
}