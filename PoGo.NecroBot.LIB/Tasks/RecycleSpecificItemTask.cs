﻿using System.Threading.Tasks;
using PoGo.NecroBot.LIB.Logging;
using PoGo.NecroBot.LIB.State;
using POGOProtos.Inventory.Item;
using POGOProtos.Networking.Responses;
using PoGo.NecroBot.LIB.Utils;

namespace PoGo.NecroBot.LIB.Tasks
{
    public class RecycleSpecificItemTask
    {
        public static async Task Execute(Session session, ItemId itemId, int count)
        {
            var response = await session.Client.Inventory.RecycleItem(itemId, count);
            if (response.Result == RecycleInventoryItemResponse.Types.Result.Success)
            {
                Logger.Write(
                    $"Recycled {count}x {itemId.ToString().Substring(4)}",
                    LogLevel.Recycling);
            }
            else
            {
                Logger.Write(
                    $"Unable to recycle {count}x {itemId.ToString().Substring(4)}",
                    LogLevel.Error);
            }
            DelayingUtils.Delay(session.LogicSettings.DelayBetweenPlayerActions, 500);
        }
    }
}