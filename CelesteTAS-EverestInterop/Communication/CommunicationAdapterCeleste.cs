using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Celeste;
using Celeste.Mod;
using StudioCommunication;
using StudioCommunication.Util;
using TAS.EverestInterop;
using TAS.EverestInterop.InfoHUD;
using TAS.Input;
using TAS.Input.Commands;
using TAS.Module;
using TAS.Utils;

#nullable enable

namespace TAS.Communication;

public sealed class CommunicationAdapterCeleste() : CommunicationAdapterBase(Location.Celeste) {
    protected override void FullReset() {
        CommunicationWrapper.Stop();
        CommunicationWrapper.Start();
    }

    protected override void OnConnectionChanged() {
        if (Connected) {
            // Stall until input initialized to avoid sending invalid hotkey data
            while (Hotkeys.KeysDict == null) {
                Thread.Sleep(UpdateRate);
            }

            CommunicationWrapper.SendCurrentBindings();
            CommunicationWrapper.SendSettings(TasSettings.StudioShared);
            CommunicationWrapper.SendCommandList();
        }
    }

    protected override void HandleMessage(MessageID messageId, BinaryReader reader) {
        switch (messageId) {
            case MessageID.FilePath:
                string path = reader.ReadString();
                LogVerbose($"Received message FilePath: '{path}'");

                InputController.StudioTasFilePath = path;
                break;

            case MessageID.Hotkey:
                var hotkey = (HotkeyID)reader.ReadByte();
                bool released = reader.ReadBoolean();
                LogVerbose($"Received message Hotkey: {hotkey} ({(released ? "released" : "pressed")})");

                Hotkeys.KeysDict[hotkey].OverrideCheck = !released;
                break;

            case MessageID.SetCustomInfoTemplate:
                var customInfoTemplate = reader.ReadString();
                LogVerbose($"Received message SetCustomInfoTemplate: '{customInfoTemplate}'");

                TasSettings.InfoCustomTemplate = customInfoTemplate;
                GameInfo.Update();
                break;

            case MessageID.ClearWatchEntityInfo:
                LogVerbose("Received message ClearWatchEntityInfo");

                InfoWatchEntity.ClearWatchEntities();
                GameInfo.Update();
                break;

            case MessageID.RecordTAS:
                var fileName = reader.ReadString();
                LogVerbose($"Received message RecordTAS: '{fileName}'");

                ProcessRecordTAS(fileName);
                break;

            case MessageID.RequestGameData:
                var gameDataType = (GameDataType)reader.ReadByte();
                object? arg = gameDataType switch {
                    GameDataType.ConsoleCommand => reader.ReadBoolean(),
                    GameDataType.SettingValue => reader.ReadString(),
                    GameDataType.SetCommandAutoCompleteEntries or
                    GameDataType.InvokeCommandAutoCompleteEntries => reader.ReadObject<(string, int)>(),
                    GameDataType.RawInfo => reader.ReadObject<(string, bool)>(),
                    _ => null,
                };
                LogVerbose($"Received message RequestGameData: '{gameDataType}' ('{arg ?? "<null>"}')");

                // Gathering data from the game can sometimes take a while (and cause a timeout)
                Task.Run(() => {
                    try {
                        object? gameData = gameDataType switch {
                            GameDataType.ConsoleCommand => GameData.GetConsoleCommand((bool)arg!),
                            GameDataType.ModInfo => GameData.GetModInfo(),
                            GameDataType.ExactGameInfo => GameInfo.ExactStudioInfo,
                            GameDataType.SettingValue => GameData.GetSettingValue((string)arg!),
                            GameDataType.CompleteInfoCommand => AreaCompleteInfo.CreateCommand(),
                            GameDataType.ModUrl => GameData.GetModUrl(),
                            GameDataType.CustomInfoTemplate => !string.IsNullOrWhiteSpace(TasSettings.InfoCustomTemplate) ? TasSettings.InfoCustomTemplate : string.Empty,
                            GameDataType.SetCommandAutoCompleteEntries => GameData.GetSetCommandAutoCompleteEntries((((string, int))arg!).Item1, (((string, int))arg).Item2).ToArray(),
                            GameDataType.InvokeCommandAutoCompleteEntries => GameData.GetInvokeCommandAutoCompleteEntries((((string, int))arg!).Item1, (((string, int))arg).Item2).ToArray(),
                            GameDataType.RawInfo => InfoCustom.GetRawInfo(((string, bool))arg!),
                            GameDataType.GameState => GameData.GetGameState(),
                            _ => null,
                        };

                        QueueMessage(MessageID.GameDataResponse, writer => {
                            writer.Write((byte)gameDataType);

                            switch (gameDataType) {
                                case GameDataType.ConsoleCommand:
                                case GameDataType.ModInfo:
                                case GameDataType.ExactGameInfo:
                                case GameDataType.SettingValue:
                                case GameDataType.CompleteInfoCommand:
                                case GameDataType.ModUrl:
                                case GameDataType.CustomInfoTemplate:
                                    writer.Write((string?)gameData ?? string.Empty);
                                    break;

                                case GameDataType.SetCommandAutoCompleteEntries:
                                case GameDataType.InvokeCommandAutoCompleteEntries:
                                    writer.WriteObject((CommandAutoCompleteEntry[]?)gameData ?? []);
                                    break;

                                case GameDataType.RawInfo:
                                    writer.WriteObject(gameData);
                                    break;

                                case GameDataType.GameState:
                                    writer.WriteObject((GameState?)gameData);
                                    break;
                            }
                            LogVerbose($"Sent message GameDataResponse: {gameDataType} = '{gameData}'");
                        });
                    } catch (Exception ex) {
                        Logger.LogDetailed(ex, $"Failed to get game data for '{gameDataType}'");
                    }

                });
                break;

            case MessageID.RequestCommandAutoComplete:
                int hash = reader.ReadInt32();
                string commandName = reader.ReadString();
                string[] commandArgs = reader.ReadObject<string[]>();
                LogVerbose($"Received message RequestCommandAutoComplete: '{commandName}' '{string.Join(' ', commandArgs)}' ({hash})");

                var meta = Command.GetMeta(commandName);
                if (meta == null) {
                    return;
                }

                List<CommandAutoCompleteEntry> entries = [];
                bool done = false;

                // Collect entries
                Task.Run(async () => {
                    var enumerator = meta.GetAutoCompleteEntries(commandArgs);
                    while (Connected && await enumerator.MoveNextAsync().ConfigureAwait(false)) {
                        lock(entries) {
                            entries.Add(enumerator.Current);
                        }
                    }
                    done = true;
                });
                // Send entries
                Task.Run(async () => {
                    CommandAutoCompleteEntry[] entriesToWrite;

                    while (Connected && !done) {
                        lock(entries) {
                            if (entries.Count == 0) {
                                continue;
                            }

                            entriesToWrite = entries.ToArray();
                            entries.Clear();
                        }

                        QueueMessage(MessageID.CommandAutoComplete, writer => {
                            writer.Write(hash);
                            writer.WriteObject(entriesToWrite);
                            writer.Write(/*done*/false);
                        });
                        LogVerbose($"Sent message CommandAutoComplete: {entriesToWrite.Length} [incremental] ({hash})");

                        await Task.Delay(TimeSpan.FromSeconds(0.1f)).ConfigureAwait(false);
                    }

                    lock(entries) {
                        entriesToWrite = entries.ToArray();
                        entries.Clear();
                    }

                    QueueMessage(MessageID.CommandAutoComplete, writer => {
                        writer.Write(hash);
                        writer.WriteObject(entriesToWrite);
                        writer.Write(/*done*/true);
                    });
                    LogVerbose($"Sent message CommandAutoComplete: {entriesToWrite.Length} [done] ({hash})");
                });
                break;

            case MessageID.GameSettings:
                var settings = reader.ReadObject<GameSettings>();
                LogVerbose("Received message GameSettings");

                TasSettings.StudioShared = settings;
                CelesteTasModule.Instance.SaveSettings();
                break;

            default:
                LogError($"Received unknown message ID: {messageId}");
                break;
        }
    }

    public void WriteState(StudioState state) {
        QueueMessage(MessageID.State, writer => writer.WriteObject(state));
        // LogVerbose("Sent message State");
    }
    public void WriteUpdateLines(Dictionary<int, string> updateLines) {
        QueueMessage(MessageID.UpdateLines, writer => writer.WriteObject(updateLines));
        LogVerbose($"Sent message UpdateLines: {updateLines.Count}");
    }
    public void WriteCurrentBindings(Dictionary<int, List<int>> nativeBindings) {
        QueueMessage(MessageID.CurrentBindings, writer => writer.WriteObject(nativeBindings));
        LogVerbose($"Sent message CurrentBindings: {nativeBindings.Count}");
    }
    public void WriteRecordingFailed(RecordingFailedReason reason) {
        QueueMessage(MessageID.RecordingFailed, writer => writer.Write((byte)reason));
        LogVerbose($"Sent message RecordingFailed: {reason}");
    }
    public void WriteSettings(GameSettings settings) {
        QueueMessage(MessageID.GameSettings, writer => writer.WriteObject(settings));
        LogVerbose("Sent message GameSettings");
    }
    public void WriteCommandList(CommandInfo[] commands) {
        QueueMessage(MessageID.CommandList, writer => writer.WriteObject(commands));
        LogVerbose("Sent message CommandList");
    }

    private void ProcessRecordTAS(string fileName) {
        if (!TASRecorderUtils.Installed) {
            WriteRecordingFailed(RecordingFailedReason.TASRecorderNotInstalled);
            return;
        }
        if (!TASRecorderUtils.FFmpegInstalled) {
            WriteRecordingFailed(RecordingFailedReason.FFmpegNotInstalled);
            return;
        }

        Manager.Controller.RefreshInputs(enableRun: true);
        if (RecordingCommand.RecordingTimes.IsNotEmpty()) {
            AbortTas("Can't use StartRecording/StopRecording with \"Record TAS\"");
            return;
        }
        Manager.NextStates |= States.Enable;

        int totalFrames = Manager.Controller.Inputs.Count;
        if (totalFrames <= 0) return;

        TASRecorderUtils.StartRecording(fileName);
        TASRecorderUtils.SetDurationEstimate(totalFrames);

        if (!Manager.Controller.Commands.TryGetValue(0, out var commands)) {
            return;
        }
        bool startsWithConsoleLoad = commands.Any(c =>
            c.Is("Console") &&
            c.Args.Length >= 1 &&
            ConsoleCommand.LoadCommandRegex.Match(c.Args[0].ToLower()) is { Success: true });

        if (startsWithConsoleLoad) {
            // Restart the music when we enter the level
            Audio.SetMusic(null, startPlaying: false, allowFadeOut: false);
            Audio.SetAmbience(null, startPlaying: false);
            Audio.BusStopAll(Buses.GAMEPLAY, immediate: true);
        }
    }

    protected override void LogInfo(string message) => Logger.Log(LogLevel.Info, "CelesteTAS/StudioCom", message);
    protected override void LogVerbose(string message) => Logger.Log(LogLevel.Verbose, "CelesteTAS/StudioCom", message);
    protected override void LogError(string message) => Logger.Log(LogLevel.Error, "CelesteTAS/StudioCom", message);
}