﻿using PoGo.NecroBot.Logic.Event;

namespace NecroBot2.Logic.Event
{
    public class NoticeEvent : IEvent
    {
        public string Message = "";

        public override string ToString()
        {
            return Message;
        }
    }
}