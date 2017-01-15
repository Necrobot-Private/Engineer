﻿using MahApps.Metro.Controls;
using PoGo.Necrobot.Window.Model;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.Event.Inventory;
using PoGo.NecroBot.Logic.Event.Player;
using PoGo.NecroBot.Logic.Event.UI;
using PoGo.NecroBot.Logic.State;
using POGOProtos.Inventory.Item;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PoGo.Necrobot.Window
{
    public partial class MainClientWindow
    {
        #region UI Event handler

        public void UIUpdateSafe(Action action)
        {
            try
            {
                this.Invoke(() => action);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }
        public void OnBotEvent(FavoriteEvent ev)
        {
            this.datacontext.PokemonList.OnFavorited(ev);
        }
        public void OnBotEvent(FinishUpgradeEvent e)
        {
            this.datacontext.PokemonList.OnUpgradeEnd(e);
        }
        public void OnBotEvent(UpgradePokemonEvent e)
        {
            this.datacontext.PokemonList.OnUpgraded(e);
        }
        public void OnBotEvent(PokemonEvolveEvent ev)
        {
            this.datacontext.PokemonList.OnEvolved(ev);
        }
        public void OnBotEvent(PokemonCaptureEvent inventory)
        {
            this.datacontext.Sidebar.AddOrUpdate(new CatchPokemonViewModel(inventory));
        }
        public void OnBotEvent(LoginEvent ev)
        {
            this.Dispatcher.Invoke(() =>
            {
                this.datacontext.Reset();

                lblAccount.Content = currentSession.Translation.GetTranslation(NecroBot.Logic.Common.TranslationString.LoggingIn, ev.AuthType, ev.Username);
            });

        }
        public void OnBotEvent(InventoryRefreshedEvent inventory)
        {

            if (currentSession.Profile == null || currentSession.Profile.PlayerData == null) return;

            var data = inventory.Inventory;

            var maxPokemonStogare = currentSession.Profile?.PlayerData?.MaxPokemonStorage;
            var maxItemStogare = currentSession.Profile?.PlayerData?.MaxItemStorage;
            var pokemons = data.InventoryDelta.InventoryItems
                .Select(x => x.InventoryItemData?.PokemonData)
                .Where(x => x != null && !x.IsEgg)
                .ToList();


            var items = data.InventoryDelta.InventoryItems.Select(x => x.InventoryItemData?.Item).Where(x => x != null).ToList();
            this.datacontext.MaxItemStogare = maxItemStogare.Value;
            this.datacontext.ItemsList.Update(items);
            this.datacontext.PokemonList.Update(pokemons, inventory.Candies, inventory.PokemonSettings);
            this.datacontext.RaisePropertyChanged("PokemonTabHeader");
            this.datacontext.RaisePropertyChanged("ItemsTabHeader");
            this.datacontext.RaisePropertyChanged("MaxItemStogare");
            UIUpdateSafe(() =>
             {
                 tabPokemons.Header = $"   POKEMONS ({this.datacontext.Pokemons.Count}/{maxPokemonStogare})  ";
                 //tabItems.Header = $"   POKEMONS ({this.datacontext.Pokemons.Count}/{maxPokemonStogare})  ";

             });
        }

        public void OnBotEvent(InventoryItemUpdateEvent e)
        {
            this.datacontext.ItemsList.Update(new List<ItemData> { e.Item });
            this.datacontext.RaisePropertyChanged("ItemsTabHeader");
        }

        public void OnBotEvent(EncounteredEvent e)
        {
            this.datacontext.SnipeList.OnSnipeData(e);
        }

        public void OnBotEvent(LoggedEvent userLogged)
        {
            this.datacontext.UI.PlayerStatus = "Playing";
            this.datacontext.UI.PlayerName = userLogged.Profile.PlayerData.Username;
            this.datacontext.RaisePropertyChanged("UI");
            this.Dispatcher.Invoke(() =>
            {
                lblAccount.Content = $"{this.datacontext.UI.PlayerStatus} as : {this.datacontext.UI.PlayerName}";

            });
        }
        public void OnBotEvent(ProfileEvent profile)
        {
            var stats = profile.Stats;

            this.datacontext.PlayerInfo.Exp = stats.FirstOrDefault(x => x.Experience > 0).Experience;
            this.datacontext.PlayerInfo.LevelExp = stats.FirstOrDefault(x => x.NextLevelXp > 0).NextLevelXp;

            this.playerProfile = profile.Profile;

            this.datacontext.UI.PlayerStatus = "Playing";
            this.datacontext.UI.PlayerName = profile.Profile.PlayerData.Username;
            this.datacontext.RaisePropertyChanged("UI");

            lblAccount.Content = $"{this.datacontext.UI.PlayerStatus} as : {this.datacontext.UI.PlayerName}";

        }
        public void OnBotEvent(TransferPokemonEvent transferedPkm)
        {
            this.datacontext.PokemonList.OnTransfer(transferedPkm);
        }
        public void OnBotEvent(StatusBarEvent e)
        {
            lblStatus.Text = e.Message;
        }
        public void OnBotEvent(FortUsedEvent ev)
        {
            this.botMap.MarkFortAsLooted(ev.Id);
        }
        public void OnBotEvent(PokeStopListEvent ev)
        {
            this.botMap.OnPokestopEvent(ev.Forts);
        }
        public void OnBotEvent(UpdatePositionEvent ev)
        {
            this.botMap.UpdatePlayerPosition(ev.Latitude, ev.Longitude);
        }
        internal void HandleBotEvent(IEvent evt)
        {
            dynamic eve = evt;

            Task.Run(() =>
            {
                this.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        OnBotEvent(eve);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex.Message);
                    }
                });
            });
    }
    #endregion

}
}
