﻿using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ConanExilesUpdater.Models;
using Serilog;
using System;
using System.Linq;
using System.IO;
using System.Collections.Concurrent;

namespace ConanExilesUpdater.Services
{
    public class GeneralServices
    {
        private readonly Settings _settings;
        private CancellationTokenSource _cancellationTokenSource;
        private CancellationToken _token;
        private readonly DiscordService _discordClient;
        private readonly TwitchService _twitchService;

        public GeneralServices(Settings settings, DiscordService discord, TwitchService twitch)
        {
            _settings = settings;
            _discordClient = discord;
            _twitchService = twitch;
        }

        public void StartServices()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _token = _cancellationTokenSource.Token;

            var tasks = new ConcurrentBag<Task>();

            if (_settings.General.ShouldRestartConanOnNotRunning == true)
            {
                Log.Information("Starting To Monitor Server is Running");
                Task sR = Task.Factory.StartNew(() =>
                {
                    MonitorServerRunning(_token);
                }, _token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                tasks.Add(sR);
            }

            if (_settings.Conan.RaidingProtectionHoursEnabled)
            {
                Log.Information("Setting up Raiding Hours Protection");
                Task rP = Task.Factory.StartNew(() =>
                {
                    RaidingProtection(_token);
                }, _token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                tasks.Add(rP);
            }
            Task.Run(()=>{
            try
            {
                Task.WaitAll(tasks.ToArray());
            }
            catch (AggregateException ae)
            {
                Log.Error("Inner Task Exception: {exception} in General Services", ae.Message);
            }
            catch (Exception e)
            {
                Log.Error("Exception: {exception} in General Services", e.Message);
            }
            });
        }

        public void StopServices()
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                Log.Information("Monitor Service has been stopped successfully");
            }
        }


        #region Monitor Server Running
        private void MonitorServerRunning(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Thread.Sleep(30 * 1000);
                    var process = Process.GetProcesses().Where(c => c.ProcessName.Contains("ConanSandboxServer")).FirstOrDefault();
                    if (process != null)
                    {
                        var startTime = process.StartTime;
                        if (_settings.General.RestartServerAfterHours == 0) continue;
                        if (startTime.AddHours(_settings.General.RestartServerAfterHours) <= DateTime.Now)
                        {
                            if (_settings.Update.AnnounceTwitch || _settings.Update.AnnounceDiscord)
                            {
                                var runningTime = DateTime.Now.Subtract(startTime);
                                var announceMessage = $"Conan Server Automatic Restarts are set to run every {_settings.General.RestartServerAfterHours} Hours. The server has been up for {Math.Round(runningTime.TotalHours, 2)} H {runningTime.Minutes} M. The Server will restart in {_settings.Update.AnnounceMinutesBefore} {(_settings.Update.AnnounceMinutesBefore == 1 ? "Minute" : "Minutes")}.";
                                if (_discordClient != null)
                                    _discordClient.SendMessage(announceMessage);
                                if (_twitchService != null)
                                    _twitchService.SendMessage(announceMessage);
                            }
                            if (_settings.Update.AnnounceMinutesBefore > 0)
                            {
                                Thread.Sleep(_settings.Update.AnnounceMinutesBefore * 60 * 1000);
                            }
                            Utils.TerminateServer();
                            Log.Information("Server exceeded maximum specified running time, and a restart request was successfully made.");
                        }
                    }
                    else
                    {
                        Log.Information("Conan Server Not Detected - Launching Now");
                        var processStartInfo = new ProcessStartInfo
                        {
                            FileName = $"{_settings.Conan.FolderPath}{_settings.Conan.Executable}",
                            Arguments = $"{_settings.Conan.StartupParameters} -log",
                            RedirectStandardOutput = false,
                            UseShellExecute = false
                        };
                        Process.Start(processStartInfo);

                        if (_settings.Update.AnnounceTwitch || _settings.Update.AnnounceDiscord)
                        {
                            var announceMessage = $"Conan Server was not detected as running. Restarting now. The server should show as being online in 2-3 Minutes.";
                            if (_discordClient != null)
                                _discordClient.SendMessage(announceMessage);
                            if (_twitchService != null)
                                _twitchService.SendMessage(announceMessage);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Error in Application Monitor: {exception}", e.Message);
                }
            }
        }
        #endregion

        #region Raiding Protection

        private void RaidingProtection(CancellationToken token)
        {
            var configFolder = $"{_settings.Conan.FolderPath}ConanSandbox\\Saved\\Config\\WindowsServer";
            if (!Directory.Exists(configFolder))
            {
                Log.Error("No server saved folder exists. You must run the server once before Raiding protection can be enabled");
                return;
            }
            var serverSettings = new INIFile(Path.Combine(configFolder, "ServerSettings.ini"));
            bool enabled = false;
            while (!token.IsCancellationRequested)
            {
                Thread.Sleep(60 * 1000);
                var dt = DateTime.Now;
                if (!enabled)
                {
                    if (dt.Hour == _settings.Conan.RaidingStartHour)
                    {
                        serverSettings.Refresh();
                        var setting = serverSettings.GetValue("ServerSettings", "CanDamagePlayerOwnedStructures", "False");
                        if (setting.Equals("False"))
                        {
                            serverSettings.SetValue("ServerSettings", "CanDamagePlayerOwnedStructures", "True");
                            serverSettings.Flush();
                        }
                        enabled = true;
                        Log.Information("Successfully Enabled Building Raiding for {length} hours", _settings.Conan.RaidingLengthInHours);
                    }
                }
                else
                {
                    if (dt.AddHours(_settings.Conan.RaidingLengthInHours) <= dt)
                    {
                        serverSettings.Refresh();
                        var setting = serverSettings.GetValue("ServerSettings", "CanDamagePlayerOwnedStructures", "True");
                        if (setting.Equals("True"))
                        {
                            serverSettings.SetValue("ServerSettings", "CanDamagePlayerOwnedStructures", "False");
                            serverSettings.Flush();
                        }
                        enabled = false;
                        Log.Information("Successfully disabled Building raiding until {hour}:00", _settings.Conan.RaidingStartHour);
                    }

                }
            }
        }

        #endregion
    }
}
