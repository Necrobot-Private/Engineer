﻿using MahApps.Metro.Controls;
using Microsoft.Win32;
using PoGo.NecroBot.Logic.Model.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.State;
using PoGo.Necrobot.Window.Win32;
using PoGo.NecroBot.Logic.Event.Player;
using PoGo.Necrobot.Window.Model;
using PoGo.NecroBot.Logic;
using PoGo.NecroBot.Logic.Logging;

namespace PoGo.Necrobot.Window
{
    /// <summary>
    /// Interaction logic for SplashScreen.xaml
    /// </summary>
    public partial class MainClientWindow : MetroWindow
    {

        public MainClientWindow()
        {
            InitializeComponent();

            datacontext = new Model.DataContext()
            {
                PlayerInfo = new PlayerInfoModel() { Exp = 0 }
            };

            this.DataContext = datacontext;

        }


        private void Grid_Loaded(object sender, RoutedEventArgs e)
        {

        }

        private void MetroWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ConsoleHelper.AllocConsole();
            UILogger logger = new UILogger();
            logger.LogToUI = this.LogToConsoleTab;

            Logger.AddLogger(logger, string.Empty);

            Task.Run(() =>
            {
                NecroBot.CLI.Program.RunBotWithParameters(this.OnBotStartedEventHandler, new string[] { });
            });
            ConsoleHelper.HideConsoleWindow();
        }
        private DateTime lastClearLog = DateTime.Now;
        private void LogToConsoleTab(string message, LogLevel level, string color)
        {

            if(color == "Black" && level == LogLevel.LevelUp) color = "DarkCyan";

            Dictionary<LogLevel, string> colors = new Dictionary<LogLevel, string>()
            {
                { LogLevel.Error, "red" },
                { LogLevel.Caught, "green" },
                { LogLevel.Info, "DarkCyan" } ,
                { LogLevel.Warning, "DarkYellow" } ,
                { LogLevel.Pokestop,"Cyan" }  ,
                { LogLevel.Farming,"Magenta" },
                { LogLevel.Sniper,"White" },
                { LogLevel.Recycling,"DarkMagenta" },
                { LogLevel.Flee,"DarkYellow" },
                { LogLevel.Transfer,"DarkGreen" },
                { LogLevel.Evolve,"DarkGreen" },
                { LogLevel.Berry,"DarkYellow" },
                { LogLevel.Egg,"DarkYellow" },
                { LogLevel.Debug,"Gray" },
                { LogLevel.Update,"White" },
                { LogLevel.New,"Green" },
                { LogLevel.SoftBan,"Red" },
                { LogLevel.LevelUp,"Magenta" },
                { LogLevel.Gym,"Magenta" },
                { LogLevel.Service ,"White" }
            };

            if (string.IsNullOrEmpty(color) || color== "Black") color = colors[level];

            this.Invoke(() =>
            {
                if (lastClearLog.AddMinutes(15) < DateTime.Now)
                {
                    consoleLog.Document.Blocks.Clear();
                    lastClearLog = DateTime.Now;
                }
                if (string.IsNullOrEmpty(color) || color == "Black") color = "white";

                consoleLog.AppendText(message + "\r\n", color);

                consoleLog.ScrollToEnd();
            });
        }

        private void OnBotStartedEventHandler(ISession session, StatisticsAggregator stat)
        {
            session.EventDispatcher.EventReceived += HandleBotEvent;
            stat.GetCurrent().DirtyEvent += OnPlayerStatisticChanged;
            this.currentSession = session;
            this.playerStats = stat;
            this.ctrPokemonInventory.Session = session;
            this.ctrlItemControl.Session = session;
            this.datacontext.PokemonList.Session = session;
            botMap.SetDefaultPosition(session.Settings.DefaultLatitude, session.Settings.DefaultLongitude);
        }

        private void OnPlayerStatisticChanged()
        {
            var stat = this.playerStats.GetCurrent();

            this.datacontext.PlayerInfo.Runtime = this.playerStats.GetCurrent().FormatRuntime();
            this.datacontext.PlayerInfo.EXPPerHour = (int)(stat.TotalExperience / stat.GetRuntime());
            this.datacontext.PlayerInfo.PKMPerHour = (int)(stat.TotalPokemons / stat.GetRuntime());
            this.datacontext.PlayerInfo.TimeToLevelUp = $"{this.playerStats.GetCurrent().StatsExport.HoursUntilLvl:00}h :{this.playerStats.GetCurrent().StatsExport.MinutesUntilLevel:00}m";
            this.datacontext.PlayerInfo.Level = this.playerStats.GetCurrent().StatsExport.Level;
            this.datacontext.PlayerInfo.Startdust = this.playerStats.GetCurrent().TotalStardust;
        }

        private void PokemonInventory_OnPokemonItemSelected(PokemonDataViewModel selected)
        {
            var numberSelected = datacontext.PokemonList.Pokemons.Count(x => x.IsSelected);
            lblCount.Text = $"Select : {numberSelected}";
        }
        bool isConsoleShowing = false;
        private void menuConsole_Click(object sender, RoutedEventArgs e)
        {
            if(isConsoleShowing)
            {
                consoleMenuText.Text= "Show Console";
                ConsoleHelper.HideConsoleWindow();
            }
            else
            {

                consoleMenuText.Text = "Close Console";
                ConsoleHelper.ShowConsoleWindow();

            }

            isConsoleShowing = !isConsoleShowing;
        }
        
        private void menuSetting_Click(object sender, RoutedEventArgs e)
        {
            var configWindow = new AppConfigWindow(this, System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "config\\config.json"));
            configWindow.ShowDialog();
        }
    }
}
