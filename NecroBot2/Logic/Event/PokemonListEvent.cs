﻿#region using directives

using System;
using System.Collections.Generic;
using POGOProtos.Data;
using PoGo.NecroBot.Logic.Event;

#endregion

namespace NecroBot2.Logic.Event
{
    public class PokemonListEvent : IEvent
    {
        public List<Tuple<PokemonData, double, int>> PokemonList;
    }
}