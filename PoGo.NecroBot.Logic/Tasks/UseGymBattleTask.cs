#region using directives

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.PoGoUtils;
using PoGo.NecroBot.Logic.State;
using POGOProtos.Inventory.Item;
using POGOProtos.Map.Fort;
using PoGo.NecroBot.Logic.Logging;
using POGOProtos.Networking.Responses;
using POGOProtos.Enums;
using PoGo.NecroBot.Logic.Event.Gym;
using PokemonGo.RocketAPI.Extensions;
using POGOProtos.Data;
using POGOProtos.Data.Battle;
using PokemonGo.RocketAPI.Exceptions;

#endregion

namespace PoGo.NecroBot.Logic.Tasks
{ 
    public class UseGymBattleTask
    {
        private static int _startBattleCounter = 3;
        private static readonly bool _logTimings = false;

        public static async Task Execute(ISession session, CancellationToken cancellationToken, FortData gym, FortDetailsResponse fortInfo)
        {
            if (!session.LogicSettings.GymConfig.Enable || gym.Type != FortType.Gym) return;

            cancellationToken.ThrowIfCancellationRequested();
            var distance = session.Navigation.WalkStrategy.CalculateDistance(session.Client.CurrentLatitude, session.Client.CurrentLongitude, gym.Latitude, gym.Longitude);
            if (fortInfo != null)
            {
                session.EventDispatcher.Send(new GymWalkToTargetEvent()
                {
                    Name = fortInfo.Name,
                    Distance = distance,
                    Latitude = fortInfo.Latitude,
                    Longitude = fortInfo.Longitude
                });

                var fortDetails = await session.Client.Fort.GetGymDetails(gym.Id, gym.Latitude, gym.Longitude);

                if (fortDetails.Result == GetGymDetailsResponse.Types.Result.Success)
                {
                    var player = session.Profile.PlayerData;
                    await EnsureJoinTeam(session, player);

                    //Do gym tutorial - tobe coded

                    session.EventDispatcher.Send(new GymDetailInfoEvent()
                    {
                        Team = fortDetails.GymState.FortData.OwnedByTeam,
                        Point = gym.GymPoints,
                        Name = fortDetails.Name,
                    });

                    if (player.Team != TeamColor.Neutral)
                    {
                        var deployedPokemons = await session.Inventory.GetDeployedPokemons();
                        if (fortDetails.GymState.FortData.OwnedByTeam == player.Team || fortDetails.GymState.FortData.OwnedByTeam == TeamColor.Neutral)
                        {
                            //trainning logic will come here
                            await DeployPokemonToGym(session, fortInfo, fortDetails, cancellationToken);
                            if (CanTrainGym(session, gym, fortDetails, ref deployedPokemons))
                                await StartGymAttackLogic(session, fortInfo, fortDetails, gym, cancellationToken);
                        }
                        else
                        {
                            if (CanAttackGym(session, gym, ref deployedPokemons))
                                await StartGymAttackLogic(session, fortInfo, fortDetails, gym, cancellationToken);
                        }
                    }
                }
                else
                {
                    Logger.Write($"You are not level 5 yet, come back later...", LogLevel.Gym, ConsoleColor.White);
                }
            }
            else
            {
                // ReSharper disable once PossibleNullReferenceException
                Logger.Write($"Ignoring  Gym : {fortInfo.Name} - ", LogLevel.Gym, ConsoleColor.Cyan);
            }
        }

        private static async Task StartGymAttackLogic(ISession session, FortDetailsResponse fortInfo,
            GetGymDetailsResponse fortDetails, FortData gym, CancellationToken cancellationToken)
        {
            var defenders = fortDetails.GymState.Memberships.Select(x => x.PokemonData).ToList();

            if (session.Profile.PlayerData.Team != fortInfo.TeamColor)
            {
                if (session.LogicSettings.GymConfig.MaxGymLevelToAttack < GetGymLevel(gym.GymPoints))
                {
                    Logger.Write($"This is gym level {GetGymLevel(gym.GymPoints)} > {session.LogicSettings.GymConfig.MaxGymLevelToAttack} in your config. Bot walk away...", LogLevel.Gym, ConsoleColor.Red);
                    return;
                }

                if (session.LogicSettings.GymConfig.MaxDefendersToAttack < defenders.Count)
                {
                    Logger.Write($"This is gym has   {defenders.Count} defender  > {session.LogicSettings.GymConfig.MaxDefendersToAttack} in your config. Bot walk away...", LogLevel.Gym, ConsoleColor.Red);
                    return;
                }
            }

            await session.Inventory.RefreshCachedInventory();
            var badassPokemon = await session.Inventory.GetHighestCpForGym(6);
            var pokemonDatas = badassPokemon as PokemonData[] ?? badassPokemon.ToArray();
            if (defenders.Count == 0) return;

            Logger.Write("Start battle with : " + string.Join(", ", defenders.Select(x => x.PokemonId.ToString())));

            // Heal pokemon
            foreach (var pokemon in pokemonDatas)
            {
                if (pokemon.Stamina <= 0)
                    await RevivePokemon(session, pokemon);

                if (pokemon.Stamina <= 0)
                {
                    Logger.Write("You are out of revive potions! Can't resurect attacker", LogLevel.Gym, ConsoleColor.Magenta);
                    return;
                }

                if (pokemon.Stamina < pokemon.StaminaMax)
                    await HealPokemon(session, pokemon);

                if (pokemon.Stamina < pokemon.StaminaMax)
                    Logger.Write(string.Format("You are out of healing potions! {0} ({1} CP) haven't got fully healed", pokemon.PokemonId, pokemon.Cp), LogLevel.Gym, ConsoleColor.Magenta);
            }
            await Task.Delay(2000);

            var index = 0;
            bool isVictory = true;
            bool isFailedToStart = false;
            List<BattleAction> battleActions = new List<BattleAction>();
            ulong defenderPokemonId = defenders.First().Id;

            while (index < defenders.Count())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var thisAttackActions = new List<BattleAction>();

                StartGymBattleResponse result = null;
                try
                {
                    await Task.Delay(2000);
                    result = await StartBattle(session, gym, pokemonDatas, defenders.FirstOrDefault(x => x.Id == defenderPokemonId));
                }
                #pragma warning disable 0168
                catch (APIBadRequestException e)
                #pragma warning restore 0168
                {
                    Logger.Write("Can't start battle", LogLevel.Gym);
                    isFailedToStart = true;
                    isVictory = false;
                    _startBattleCounter--;

                    var newFots = await UseNearbyPokestopsTask.UpdateFortsData(session);
                    gym = newFots.FirstOrDefault(w=>w.Id == gym.Id);

                    break;
                }

                index++;
                // If we can't start battle in 10 tries, let's skip the gym
                if (result == null || result.Result == StartGymBattleResponse.Types.Result.Unset)
                {
                    session.EventDispatcher.Send(new GymErrorUnset { GymName = fortInfo.Name });
                    isVictory = false;
                    break;
                }

                if (result.Result != StartGymBattleResponse.Types.Result.Success) break;
                switch (result.BattleLog.State)
                {
                    case BattleState.Active:
                        Logger.Write($"Time to start Attack Mode", LogLevel.Gym, ConsoleColor.DarkYellow);
                        thisAttackActions = await AttackGym(session, cancellationToken, gym, result);
                        battleActions.AddRange(thisAttackActions);
                        break;
                    case BattleState.Defeated:
                        isVictory = false;
                        break;
                    case BattleState.StateUnset:
                        isVictory = false;
                        break;
                    case BattleState.TimedOut:
                        isVictory = false;
                        break;
                    case BattleState.Victory:
                        break;
                    default:
                        Logger.Write($"Unhandled result starting gym battle:\n{result}");
                        break;
                }

                var rewarded = battleActions.Select(x => x.BattleResults?.PlayerExperienceAwarded).Where(x => x != null);
                var lastAction = battleActions.LastOrDefault();

                if (lastAction.Type == BattleActionType.ActionTimedOut ||
                    lastAction.Type == BattleActionType.ActionUnset ||
                    lastAction.Type == BattleActionType.ActionDefeat)
                {
                    isVictory = false;
                    break;
                }

                var faintedPKM = battleActions.Where(x => x != null && x.Type == BattleActionType.ActionFaint).Select(x => x.ActivePokemonId).Distinct();
                var livePokemons = pokemonDatas.Where(x => !faintedPKM.Any(y => y == x.Id));
                var faintedPokemons = pokemonDatas.Where(x => faintedPKM.Any(y => y == x.Id));
                pokemonDatas = livePokemons.Concat(faintedPokemons).ToArray();

                if (lastAction.Type == BattleActionType.ActionVictory)
                {
                    if (lastAction.BattleResults != null)
                    {
                        var exp = lastAction.BattleResults.PlayerExperienceAwarded;
                        var point = lastAction.BattleResults.GymPointsDelta;
                        gym.GymPoints += point;
                        defenderPokemonId = unchecked((ulong)lastAction.BattleResults.NextDefenderPokemonId);

                        Logger.Write(string.Format("Exp: {0}, Gym points: {1}"/*, Next defender id: {2}"*/, exp, point, defenderPokemonId), LogLevel.Gym, ConsoleColor.Magenta);
                    }
                    continue;
                }
            }

            if (isVictory)
            {
                if (gym.GymPoints < 0)
                    gym.GymPoints = 0;
                await Execute(session, cancellationToken, gym, fortInfo);
            }

            if (isFailedToStart && _startBattleCounter > 0)
            {
                //session.ReInitSessionWithNextBot();
                await Execute(session, cancellationToken, gym, fortInfo);
            }

            if (_startBattleCounter <= 0)
                _startBattleCounter = 3;
        }

        private static async Task DeployPokemonToGym(ISession session, FortDetailsResponse fortInfo, GetGymDetailsResponse fortDetails, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var points = fortDetails.GymState.FortData.GymPoints;
            var maxCount = GetGymLevel(points);

            var availableSlots = maxCount - fortDetails.GymState.Memberships.Count();

            if (availableSlots > 0)
            {
                var deployed = await session.Inventory.GetDeployedPokemons();
                if (!deployed.Any(a => a.DeployedFortId == fortInfo.FortId))
                {
                    var pokemon = await GetDeployablePokemon(session);
                    if (pokemon != null)
                    {
                        FortDeployPokemonResponse response = null;
                        try
                        {
                            response = await session.Client.Fort.FortDeployPokemon(fortInfo.FortId, pokemon.Id);
                        }
                        catch (APIBadRequestException)
                        {
                            Logger.Write("Failed to deploy pokemon. Trying again...", LogLevel.Gym, ConsoleColor.Magenta);
                            await Execute(session, cancellationToken, fortDetails.GymState.FortData, fortInfo);
                            return;
                        }
                        if (response?.Result == FortDeployPokemonResponse.Types.Result.Success)
                        {
                            session.EventDispatcher.Send(new GymDeployEvent()
                            {
                                PokemonId = pokemon.PokemonId,
                                Name = fortDetails.Name
                            });
                            if (session.LogicSettings.GymConfig.CollectCoinAfterDeployed > 0)
                            {
                                var count = deployed.Count() + 1;
                                if (count >= session.LogicSettings.GymConfig.CollectCoinAfterDeployed)
                                {
                                    try
                                    {
                                        if (session.Profile.PlayerData.DailyBonus.NextDefenderBonusCollectTimestampMs <= DateTime.Now.ToUnixTime())
                                        {
                                            var collectDailyBonusResponse = await session.Client.Player.CollectDailyDefenderBonus();
                                            if (collectDailyBonusResponse.Result == CollectDailyDefenderBonusResponse.Types.Result.Success)
                                                Logger.Write($"Collected {count * 10} coins", LogLevel.Gym, ConsoleColor.DarkYellow);
                                            else
                                                Logger.Write($"Hmm, we have failed with gaining a reward: {collectDailyBonusResponse}", LogLevel.Gym, ConsoleColor.Magenta);
                                        }
                                        else
                                            Logger.Write($"You will be able to collect bonus at {DateTimeFromUnixTimestampMillis(session.Profile.PlayerData.DailyBonus.NextDefenderBonusCollectTimestampMs)}", LogLevel.Info, ConsoleColor.Magenta);
                                    }
                                    catch (APIBadRequestException)
                                    {
                                        Logger.Write("Can't get coins", LogLevel.Warning);
                                        //Debug.WriteLine(e.Message, "GYM");
                                        //Debug.WriteLine(e.StackTrace, "GYM");

                                        await Task.Delay(500);
                                    }
                                }
                                else
                                    Logger.Write(string.Format("You have only {0} defenders deployed but {1} required to get reward", count, session.LogicSettings.GymConfig.CollectCoinAfterDeployed), LogLevel.Gym, ConsoleColor.Magenta);
                            }
                            else
                                Logger.Write("You have disabled reward collecting in config file", LogLevel.Gym, ConsoleColor.Magenta);
                        }
                        else
                            Logger.Write(string.Format("Deploy pokemon failed with result: {0}", response.Result), LogLevel.Gym, ConsoleColor.Magenta);
                    }
                    else
                        Logger.Write($"You don't have pokemons to be deployed!", LogLevel.Gym);
                }
                else
                    Logger.Write($"You already have pokemon deployed here", LogLevel.Gym);
            }
            else
            {
                string message = string.Format("No action. No FREE slots in GYM {0}/{1} ({2})", fortDetails.GymState.Memberships.Count(), maxCount, points);
                Logger.Write(message, LogLevel.Gym, ConsoleColor.White);
            }
        }

        public static async Task RevivePokemon(ISession session, PokemonData pokemon)
        {
            var normalPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemPotion);
            var superPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemSuperPotion);
            var hyperPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemHyperPotion);

            var healPower = normalPotions * 20 + superPotions * 50 + hyperPotions * 200;

            var normalRevives = await session.Inventory.GetItemAmountByType(ItemId.ItemRevive);
            var maxRevives = await session.Inventory.GetItemAmountByType(ItemId.ItemMaxRevive);

            if ((healPower >= pokemon.StaminaMax / 2 || maxRevives == 0) && normalRevives > 0 && pokemon.Stamina <= 0)
            {
                var ret = await session.Client.Inventory.UseItemRevive(ItemId.ItemRevive, pokemon.Id);
                switch (ret.Result)
                {
                    case UseItemReviveResponse.Types.Result.Success:
                        await session.Inventory.UpdateInventoryItem(ItemId.ItemRevive, -1);
                        pokemon.Stamina = ret.Stamina;
                        session.EventDispatcher.Send(new EventUsedRevive
                        {
                            Type = "normal",
                            PokemonCp = pokemon.Cp,
                            PokemonId = pokemon.PokemonId.ToString(),
                            Remaining = (normalRevives - 1)
                        });
                        break;
                    case UseItemReviveResponse.Types.Result.ErrorDeployedToFort:
                        Logger.Write(
                            $"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                        return;
                    case UseItemReviveResponse.Types.Result.ErrorCannotUse:
                        return;
                    default:
                        return;
                }
                return;
            }

            if (maxRevives > 0 && pokemon.Stamina <= 0)
            {
                var ret = await session.Client.Inventory.UseItemRevive(ItemId.ItemMaxRevive, pokemon.Id);
                switch (ret.Result)
                {
                    case UseItemReviveResponse.Types.Result.Success:
                        await session.Inventory.UpdateInventoryItem(ItemId.ItemMaxRevive, -1);
                        pokemon.Stamina = ret.Stamina;
                        session.EventDispatcher.Send(new EventUsedRevive
                        {
                            Type = "max",
                            PokemonCp = pokemon.Cp,
                            PokemonId = pokemon.PokemonId.ToString(),
                            Remaining = (maxRevives - 1)
                        });
                        break;

                    case UseItemReviveResponse.Types.Result.ErrorDeployedToFort:
                        Logger.Write($"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                        return;

                    case UseItemReviveResponse.Types.Result.ErrorCannotUse:
                        return;

                    default:
                        return;
                }
            }
        }

        private static async Task<bool> UsePotion(ISession session, PokemonData pokemon, int normalPotions)
        {
            var ret = await session.Client.Inventory.UseItemPotion(ItemId.ItemPotion, pokemon.Id);
            switch (ret.Result)
            {
                case UseItemPotionResponse.Types.Result.Success:
                    pokemon.Stamina = ret.Stamina;
                    session.EventDispatcher.Send(new EventUsedPotion
                    {
                        Type = "normal",
                        PokemonCp = pokemon.Cp,
                        PokemonId = pokemon.PokemonId.ToString(),
                        Remaining = (normalPotions - 1)
                    });
                    break;

                case UseItemPotionResponse.Types.Result.ErrorDeployedToFort:
                    Logger.Write($"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                    return false;

                case UseItemPotionResponse.Types.Result.ErrorCannotUse:
                    return false;

                default:
                    return false;
            }
            return true;
        }

        private static async Task<bool> UseSuperPotion(ISession session, PokemonData pokemon, int superPotions)
        {
            var ret = await session.Client.Inventory.UseItemPotion(ItemId.ItemSuperPotion, pokemon.Id);
            switch (ret.Result)
            {
                case UseItemPotionResponse.Types.Result.Success:
                    pokemon.Stamina = ret.Stamina;
                    session.EventDispatcher.Send(new EventUsedPotion
                    {
                        Type = "super",
                        PokemonCp = pokemon.Cp,

                        PokemonId = pokemon.PokemonId.ToString(),
                        Remaining = (superPotions - 1)
                    });
                    break;

                case UseItemPotionResponse.Types.Result.ErrorDeployedToFort:
                    Logger.Write($"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                    return false;

                case UseItemPotionResponse.Types.Result.ErrorCannotUse:
                    return false;

                default:
                    return false;
            }
            return true;
        }

        private static async Task<bool> UseHyperPotion(ISession session, PokemonData pokemon, int hyperPotions)
        {
            var ret = await session.Client.Inventory.UseItemPotion(ItemId.ItemHyperPotion, pokemon.Id);
            switch (ret.Result)
            {
                case UseItemPotionResponse.Types.Result.Success:
                    pokemon.Stamina = ret.Stamina;
                    session.EventDispatcher.Send(new EventUsedPotion
                    {
                        Type = "hyper",
                        PokemonCp = pokemon.Cp,
                        PokemonId = pokemon.PokemonId.ToString(),
                        Remaining = (hyperPotions - 1)
                    });
                    break;

                case UseItemPotionResponse.Types.Result.ErrorDeployedToFort:
                    Logger.Write($"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                    return false;

                case UseItemPotionResponse.Types.Result.ErrorCannotUse:
                    return false;

                default:
                    return false;
            }
            return true;
        }

        private static async Task<bool> UseMaxPotion(ISession session, PokemonData pokemon, int maxPotions)
        {
            var ret = await session.Client.Inventory.UseItemPotion(ItemId.ItemMaxPotion, pokemon.Id);
            switch (ret.Result)
            {
                case UseItemPotionResponse.Types.Result.Success:
                    pokemon.Stamina = ret.Stamina;
                    session.EventDispatcher.Send(new EventUsedPotion
                    {
                        Type = "max",
                        PokemonCp = pokemon.Cp,
                        PokemonId = pokemon.PokemonId.ToString(),
                        Remaining = maxPotions
                    });
                    break;

                case UseItemPotionResponse.Types.Result.ErrorDeployedToFort:
                    Logger.Write($"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                    return false;

                case UseItemPotionResponse.Types.Result.ErrorCannotUse:
                    return false;

                default:
                    return false;
            }
            return true;
        }

        public static async Task<bool> HealPokemon(ISession session, PokemonData pokemon)
        {
            var normalPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemPotion);
            var superPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemSuperPotion);
            var hyperPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemHyperPotion);
            var maxPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemMaxPotion);

            var healPower = normalPotions * 20 + superPotions * 50 + hyperPotions * 200;

            if (healPower < (pokemon.StaminaMax - pokemon.Stamina) && maxPotions > 0)
            {
                try
                {
                    if (await UseMaxPotion(session, pokemon, maxPotions))
                    {
                        await session.Inventory.UpdateInventoryItem(ItemId.ItemMaxPotion, -1);
                        return true;
                    }
                }
                catch (APIBadRequestException)
                {
                    Logger.Write(string.Format("Heal problem with max potions ({0}) on pokemon: {1}", maxPotions, pokemon), LogLevel.Error, ConsoleColor.Magenta);
                }
            }

            while (normalPotions + superPotions + hyperPotions > 0 && (pokemon.Stamina < pokemon.StaminaMax))
            {
                if (((pokemon.StaminaMax - pokemon.Stamina) > 200 || ((normalPotions * 20 + superPotions * 50) < (pokemon.StaminaMax - pokemon.Stamina))) && hyperPotions > 0)
                {
                    if (!await UseHyperPotion(session, pokemon, hyperPotions))
                        return false;
                    hyperPotions--;
                    await session.Inventory.UpdateInventoryItem(ItemId.ItemHyperPotion, -1);
                }
                else
                if (((pokemon.StaminaMax - pokemon.Stamina) > 50 || normalPotions * 20 < (pokemon.StaminaMax - pokemon.Stamina)) && superPotions > 0)
                {
                    if (!await UseSuperPotion(session, pokemon, superPotions))
                        return false;
                    superPotions--;
                    await session.Inventory.UpdateInventoryItem(ItemId.ItemSuperPotion, -1);
                }
                else
                {
                    if (!await UsePotion(session, pokemon, normalPotions))
                        return false;
                    normalPotions--;
                    await session.Inventory.UpdateInventoryItem(ItemId.ItemPotion, -1);
                }
            }

            return pokemon.Stamina == pokemon.StaminaMax;
        }

        private static int _currentAttackerEnergy;

        // ReSharper disable once UnusedParameter.Local
        private static async Task<List<BattleAction>> AttackGym(ISession session, 
            CancellationToken cancellationToken, 
            FortData currentFortData, 
            StartGymBattleResponse startResponse)
        {
            long serverMs = startResponse.BattleLog.BattleStartTimestampMs;
            var lastActions = startResponse.BattleLog.BattleActions.ToList();

            Logger.Write($"Gym battle started; fighting trainer: {startResponse.Defender.TrainerPublicProfile.Name}", LogLevel.Gym, ConsoleColor.Green);
            Logger.Write($"We are attacking: {startResponse.Defender.ActivePokemon.PokemonData.PokemonId}", LogLevel.Gym, ConsoleColor.White);
            Console.WriteLine(Environment.NewLine);

            int loops = 0;
            List<BattleAction> emptyActions = new List<BattleAction>();
            BattleAction emptyAction = new BattleAction();
            PokemonData attacker = null;
            PokemonData defender = null;
            _currentAttackerEnergy = 0;
            long lastApiCallTime = DateTime.Now.ToUnixTime();

            while (true)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    TimedLog("Starts loop");
                    var last = lastActions.LastOrDefault();

                    if (last?.Type == BattleActionType.ActionPlayerJoin)
                    {
                        await Task.Delay(3000);
                        TimedLog("Sleep after join battle");
                    }

                    TimedLog("Getting actions");
                    var attackActionz = last == null || last.Type == BattleActionType.ActionVictory || last.Type == BattleActionType.ActionDefeat ? emptyActions : await GetActions(session, serverMs, attacker, defender, _currentAttackerEnergy);

                    TimedLog(string.Format(" Going to make attack : {0}", string.Join(", ", attackActionz.Select(s => string.Format("{0} -> {1}", s.Type, s.DurationMs)))));

                    BattleAction a2 = (last == null || last.Type == BattleActionType.ActionVictory || last.Type == BattleActionType.ActionDefeat ? emptyAction : last);
                    AttackGymResponse attackResult = null;
                    try
                    {

                        long timeBefore = DateTime.Now.ToUnixTime();
                        var attackTime = attackActionz.Sum(x => x.DurationMs);
                        int attackTimeCorrected = attackTime - (int)(timeBefore - lastApiCallTime);
                        TimedLog(string.Format("Waiting for attack to be prepared: {0} (last call was {1}, after correction {2})", attackTime, lastApiCallTime, attackTimeCorrected > 0 ? attackTimeCorrected : 0));
                        if(attackTimeCorrected > 0)
                            await Task.Delay(attackTimeCorrected);

                        if (attackActionz.Any(a => a.Type == BattleActionType.ActionSpecialAttack))
                        {
                            var damageWindow = attackActionz.Sum(s => s.DamageWindowsEndTimestampMs - s.DamageWindowsStartTimestampMs);
                            TimedLog(string.Format("Waiting extra {0}ms for damage window.", damageWindow));
                            await Task.Delay((int)damageWindow);
                        }

                        TimedLog("Start making attack");
                        attackResult = await session.Client.Fort.AttackGym(currentFortData.Id, startResponse.BattleId, attackActionz, a2);
                        TimedLog(string.Format("Finished making attack: {0}, Battle finished at: {1}", attackResult.BattleLog.BattleStartTimestampMs, attackResult.BattleLog.BattleEndTimestampMs));
                        lastApiCallTime = DateTime.Now.ToUnixTime();

                    }
                    catch (APIBadRequestException)
                    {
                        Logger.Write("Bad attack gym", LogLevel.Warning);
                        TimedLog(string.Format("Last retrieved action was: {0}", a2));
                        TimedLog(string.Format("Actions to perform were: {0}", string.Join(", ", attackActionz)));
                        TimedLog(string.Format("Attacker was: {0}, defender was: {1}", attacker, defender));

                        continue;
                    };

                    loops++;

                    if (attackResult.Result == AttackGymResponse.Types.Result.Success)
                    {
                        TimedLog("Attack success");
                        defender = attackResult.ActiveDefender?.PokemonData;
                        if (attackResult.BattleLog != null && attackResult.BattleLog.BattleActions.Count > 0)
                            lastActions.AddRange(attackResult.BattleLog.BattleActions);
                        serverMs = attackResult.BattleLog.ServerMs;

                        switch (attackResult.BattleLog.State)
                        {
                            case BattleState.Active:
                                TimedLog("Battlestate active start");
                                _currentAttackerEnergy = attackResult.ActiveAttacker.CurrentEnergy;
                                if (attacker == null)
                                    attacker = attackResult.ActiveAttacker.PokemonData;
                                if (attacker.Id != attackResult?.ActiveAttacker?.PokemonData.Id)
                                {
                                    attacker = attackResult.ActiveAttacker.PokemonData;
                                    await Task.Delay(2000);
                                    Logger.Write(string.Format("We ware fainted in battle, new attacker is: {0} ({1} CP){2}", attacker.PokemonId, attacker.Cp, Environment.NewLine), LogLevel.Info, ConsoleColor.Magenta);
                                }
                                Console.SetCursorPosition(0, Console.CursorTop - 1);
                                Logger.Write($"(GYM ATTACK) : Defender {attackResult.ActiveDefender.PokemonData.PokemonId.ToString()  } HP {attackResult.ActiveDefender.CurrentHealth} - Attacker  {attackResult.ActiveAttacker.PokemonData.PokemonId.ToString()}   HP/Sta {attackResult.ActiveAttacker.CurrentHealth}/{attackResult.ActiveAttacker.CurrentEnergy}        ");

                                break;

                            case BattleState.Defeated:
                                Logger.Write(
                                    $"We were defeated... (AttackGym)");
                                return lastActions;
                            case BattleState.TimedOut:
                                Logger.Write(
                                    $"Our attack timed out...:");
                                return lastActions;
                            case BattleState.StateUnset:
                                Logger.Write(
                                    $"State was unset?: {attackResult}");
                                return lastActions;

                            case BattleState.Victory:
                                Logger.Write(
                                    $"We were victorious!: ");
                                return lastActions;
                            default:
                                Logger.Write(
                                    $"Unhandled attack response: {attackResult}");
                                continue;
                        }
                        Debug.WriteLine($"{attackResult}", "GYM: " + DateTime.Now.ToUnixTime());
                    }
                    else
                    {
                        Logger.Write($"Unexpected attack result:\n{attackResult}");
                        break;
                    }

                    TimedLog("Finished attack");
                }
                catch (APIBadRequestException e)
                {
                    Logger.Write("Bad request send to server -", LogLevel.Warning);
                    TimedLog("NOT finished attack");
                    TimedLog(e.Message);
                };
            }
            return lastActions;

        }

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static DateTime DateTimeFromUnixTimestampMillis(long millis)
        {
            return UnixEpoch.AddMilliseconds(millis);
        }

        //private static int _pos;
        public static async Task<List<BattleAction>> GetActions(ISession sessison, long serverMs, PokemonData attacker, PokemonData defender, int energy)
        {
            Random rnd = new Random();
            List<BattleAction> actions = new List<BattleAction>();
            DateTime now = DateTimeFromUnixTimestampMillis(serverMs);
            //Logger.Write($"AttackGym Count: {_pos}");

            var inventory = sessison.Inventory;

            if (attacker != null && defender != null)
            {
                //var move1 = PokemonMoveMetaRegistry.GetMeta(attacker.Move1);
                //var move2 = PokemonMoveMetaRegistry.GetMeta(attacker.Move2);
                //  Logger.Write($"Retrieved Move Metadata, Move1: {move1.GetTime()} - Move2: {move2.GetTime()}");

                var moveSetting = await inventory.GetMoveSetting(attacker.Move1);
                var specialMove = await inventory.GetMoveSetting(attacker.Move2);

                BattleAction action2 = new BattleAction();
                if (Math.Abs(specialMove.EnergyDelta) <= energy)
                {
                    now = now.AddMilliseconds(specialMove.DurationMs);
                    action2.Type = BattleActionType.ActionSpecialAttack;
                    action2.DurationMs = specialMove.DurationMs;

                    action2.DamageWindowsStartTimestampMs = specialMove.DamageWindowStartMs;
                    action2.DamageWindowsEndTimestampMs = specialMove.DamageWindowEndMs;
                }
                else
                {
                    now = now.AddMilliseconds(moveSetting.DurationMs);
                    action2.Type = BattleActionType.ActionAttack;
                    action2.DurationMs = moveSetting.DurationMs;

                    action2.DamageWindowsStartTimestampMs = moveSetting.DamageWindowStartMs;
                    action2.DamageWindowsEndTimestampMs = moveSetting.DamageWindowEndMs;
                }
                action2.ActionStartMs = now.ToUnixTime();
                action2.TargetIndex = -1;
                if (attacker.Stamina > 0)
                    action2.ActivePokemonId = attacker.Id;
                action2.TargetPokemonId = defender.Id;

                actions.Add(action2);
                return actions;
            }
            BattleAction action1 = new BattleAction();
            now = now.AddMilliseconds(500);
            action1.Type = BattleActionType.ActionAttack;
            action1.DurationMs = 500;
            action1.ActionStartMs = now.ToUnixTime();
            action1.TargetIndex = -1;
            if (defender != null)
                action1.ActivePokemonId = attacker.Id;

            actions.Add(action1);

            return actions;

        }

        private static async Task<StartGymBattleResponse> StartBattle(ISession session, FortData currentFortData, IEnumerable<PokemonData> attackers, PokemonData defender)
        {

            IEnumerable<PokemonData> currentPokemons = attackers;
            var gymInfo = await session.Client.Fort.GetGymDetails(currentFortData.Id, currentFortData.Latitude, currentFortData.Longitude);
            if (gymInfo.Result != GetGymDetailsResponse.Types.Result.Success)
            {
                return null;
            }

            var pokemonDatas = currentPokemons as PokemonData[] ?? currentPokemons.ToArray();
            //var defendingPokemon = gymInfo.GymState.Memberships.First().PokemonData.Id;
            var attackerPokemons = pokemonDatas.Select(pokemon => pokemon.Id);
            var attackingPokemonIds = attackerPokemons as ulong[] ?? attackerPokemons.ToArray();

            //Logger.Write(
            //    $"Attacking Gym: {gymInfo.Name}, DefendingPokemons: { string.Join(", ", gymInfo.GymState.Memberships.Select(p => p.PokemonData.PokemonId).ToList()) }, Attacking: { string.Join(", ", attackers.Select(s=>s.PokemonId)) }"
            //    , LogLevel.Gym, ConsoleColor.Magenta
            //    );
            try
            {
                var result = await session.Client.Fort.StartGymBattle(currentFortData.Id, defender.Id, attackingPokemonIds);

                if (result.Result == StartGymBattleResponse.Types.Result.Success)
                {
                    switch (result.BattleLog.State)
                    {
                        case BattleState.Active:
                            Logger.Write("Start new battle...");
                            //session.EventDispatcher.Send(new GymBattleStarted { GymName = gymInfo.Name });
                            return result;
                        case BattleState.Defeated:
                            Logger.Write($"We were defeated in battle.");
                            return result;
                        case BattleState.Victory:
                            Logger.Write($"We were victorious");
                            //_pos = 0;
                            return result;
                        case BattleState.StateUnset:
                            Logger.Write($"Error occoured: {result.BattleLog.State}");
                            break;
                        case BattleState.TimedOut:
                            Logger.Write($"Error occoured: {result.BattleLog.State}");
                            break;
                        default:
                            Logger.Write($"Unhandled occoured: {result.BattleLog.State}");
                            break;
                    }
                }
                else if (result.Result == StartGymBattleResponse.Types.Result.ErrorGymBattleLockout)
                {
                    return result;
                }
                else if (result.Result == StartGymBattleResponse.Types.Result.ErrorAllPokemonFainted)
                {
                    return result;
                }
                else if (result.Result == StartGymBattleResponse.Types.Result.Unset)
                {
                    return result;
                }
                return result;
            }
            catch (APIBadRequestException e)
            {
                TimedLog("Gym details: " + gymInfo);
                throw e;
            }
        }

        private static async Task EnsureJoinTeam(ISession session, PlayerData player)
        {
            if (session.Profile.PlayerData.Team == TeamColor.Neutral)
            {
                var defaultTeam = (TeamColor)Enum.Parse(typeof(TeamColor), session.LogicSettings.GymConfig.DefaultTeam);
                var teamResponse = await session.Client.Player.SetPlayerTeam(defaultTeam);
                if (teamResponse.Status == SetPlayerTeamResponse.Types.Status.Success)
                {
                    player.Team = defaultTeam;
                }

                session.EventDispatcher.Send(new GymTeamJoinEvent()
                {
                    Team = defaultTeam,
                    Status = teamResponse.Status
                });
            }
        }

        //private bool CanVisitGym()
        //{
        //    return true;
        //}

        internal static int GetGymLevel(double points)
        {
            if (points < 2000) return 1;
            else
            if (points < 4000) return 2;
            else
                if (points < 8000) return 3;
            else if (points < 12000) return 4;
            else if (points < 16000) return 5;
            else if (points < 20000) return 6;
            else if (points < 30000) return 7;
            else if (points < 40000) return 8;
            else if (points < 50000) return 10;
            return 10;
        }

        internal static int GetGymMaxPointsOnLevel(int lvl)
        {
            if (lvl == 1) return 2000 - 1;
            else
            if (lvl == 2) return 4000 - 1;
            else
                if (lvl == 3) return 8000 - 1;
            else if (lvl == 4) return 12000 - 1;
            else if (lvl == 5) return 16000 - 1;
            else if (lvl == 6) return 20000 - 1;
            else if (lvl == 7) return 30000 - 1;
            else if (lvl == 8) return 40000 - 1;
            else if (lvl == 9) return 50000 - 1;
            return 52000;
        }

        internal static bool CanAttackGym(ISession session, FortData fort, ref IEnumerable<PokemonData> deployedPokemons)
        {
            if (!session.LogicSettings.GymConfig.EnableAttackGym)
                return false;
            if (fort.OwnedByTeam == session.Profile.PlayerData.Team)
                return false;
            if (GetGymLevel(fort.GymPoints) > session.LogicSettings.GymConfig.MaxGymLevelToAttack)
                return false;
            if (session.LogicSettings.GymConfig.DontAttackAfterCoinsLimitReached && deployedPokemons.Count() >= session.LogicSettings.GymConfig.CollectCoinAfterDeployed)
                return false;
            return true;
        }

        internal static bool CanTrainGym(ISession session, FortData fort, GetGymDetailsResponse gymDetails, ref IEnumerable<PokemonData> deployedPokemons)
        {
            bool isDeployed = deployedPokemons.Any(a => a.DeployedFortId == fort.Id);
            if (gymDetails != null && GetGymLevel(fort.GymPoints) > gymDetails.GymState.Memberships.Count && !isDeployed) // free slot should be used always but not always we know that...
                return true;
            if (!session.LogicSettings.GymConfig.EnableGymTraining)
                return false;
            if (fort.OwnedByTeam != session.Profile.PlayerData.Team)
                return false;
            if (!session.LogicSettings.GymConfig.TrainAlreadyDefendedGym && isDeployed)
                return false;
            if (GetGymLevel(fort.GymPoints) > session.LogicSettings.GymConfig.MaxGymLvlToTrain)
                return false;
            if (GetGymMaxPointsOnLevel(GetGymLevel(fort.GymPoints)) - fort.GymPoints > session.LogicSettings.GymConfig.TrainGymWhenMissingMaxPoints)
                return false;
            if (session.LogicSettings.GymConfig.DontAttackAfterCoinsLimitReached && deployedPokemons.Count() >= session.LogicSettings.GymConfig.CollectCoinAfterDeployed)
                return false;
            return true;
        }

        private static async Task<PokemonData> GetDeployablePokemon(ISession session)
        {
            PokemonData pokemon = null;
            List<ulong> excluded = new List<ulong>();

            while (pokemon == null)
            {
                var pokemonList = (await session.Inventory.GetPokemons()).ToList();
                pokemonList = pokemonList
                    .Where(w => !excluded.Contains(w.Id) && w.Id != session.Profile.PlayerData.BuddyPokemon?.Id && (session.LogicSettings.GymConfig.HealDefendersBeforeApplyToGym || w.Stamina == w.StaminaMax))
                    .OrderByDescending(p => p.Cp)
                    .Skip(Math.Min(pokemonList.Count - 1, session.LogicSettings.GymConfig.NumberOfTopPokemonToBeExcluded))
                    .ToList();

                if (pokemonList.Count == 0)
                    return null;

                if (pokemonList.Count == 1) pokemon = pokemonList.FirstOrDefault();
                if (session.LogicSettings.GymConfig.UseRandomPokemon && pokemon == null)
                {
                    pokemon = pokemonList.ElementAt(new Random().Next(0, pokemonList.Count - 1));
                }

                pokemon = pokemonList.FirstOrDefault(p => p.Cp <= session.LogicSettings.GymConfig.MaxCPToDeploy && PokemonInfo.GetLevel(p) <= session.LogicSettings.GymConfig.MaxLevelToDeploy && string.IsNullOrEmpty(p.DeployedFortId));

                if (session.LogicSettings.GymConfig.HealDefendersBeforeApplyToGym)
                {
                    if (pokemon.Stamina == 0)
                        await RevivePokemon(session, pokemon);

                    if (pokemon.Stamina < pokemon.StaminaMax)
                        await HealPokemon(session, pokemon);

                    if (pokemon.Stamina < pokemon.StaminaMax)
                    {
                        excluded.Add(pokemon.Id);
                        pokemon = null;
                    }
                }
            }
            return pokemon;
        }

        private static void TimedLog(string message)
        {
            if(_logTimings)
                Logger.Write(string.Format("{0} {1}", DateTime.Now.ToUnixTime(), message), LogLevel.Gym, ConsoleColor.Magenta);
        }
    }
}