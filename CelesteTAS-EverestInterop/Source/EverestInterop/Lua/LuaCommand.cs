﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Celeste.Mod;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod.Cil;
using TAS.Input.Commands;
using TAS.Module;
using TAS.Utils;

namespace TAS.EverestInterop.Lua;

public static class LuaCommand {
    private static bool consolePrintLog;
    private const string commandName = "evallua";
    private static readonly Regex commandAndSeparatorRegex = new(@$"^{commandName}[ |,]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly FieldInfo DebugRClogFieldInfo = typeof(Commands).GetFieldInfo("debugRClog");

    [Load]
    private static void Load() {
        HookEverestDebugRc();
    }

    private static void HookEverestDebugRc() {
        var methods = typeof(Everest.DebugRC).GetNestedType("<>c", BindingFlags.NonPublic)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var method in methods) {
            var methodBody = method.GetMethodBody();
            if (methodBody == null) {
                continue;
            }

            foreach (var localVariable in methodBody.LocalVariables) {
                if (localVariable.LocalType?.FullName != "Monocle.Commands+CommandData") {
                    continue;
                }

                method.IlHook((cursor, _) => {
                    // insert codes after "rawCommand.Split(new[] {' ', ','}, StringSplitOptions.RemoveEmptyEntries);"
                    if (cursor.TryGotoNext(MoveType.After, instr => instr.MatchCallvirt<string>("Split"))) {
                        cursor.Emit(OpCodes.Ldloc_0).EmitDelegate<Func<string[], string, string[]>>(
                            (commandAndArgs, rawCommand) => {
                                if (commandAndArgs[0].ToLower() == commandName && commandAndArgs.Length >= 2) {
                                    return new[] {commandName, commandAndSeparatorRegex.Replace(rawCommand, "")};
                                }

                                return commandAndArgs;
                            });
                    }
                });

                return;
            }
        }
    }

    [Monocle.Command(commandName, "Evaluate lua code (CelesteTAS)")]
    private static void EvalLua(string code) {
        string firstHistory = Engine.Commands.commandHistory.FirstOrDefault();
        if (DebugRClogFieldInfo.GetValue(Engine.Commands) == null &&
            firstHistory?.StartsWith(commandName, StringComparison.InvariantCultureIgnoreCase) == true) {
            code = commandAndSeparatorRegex.Replace(firstHistory, "");
        }

        consolePrintLog = true;
        EvalLuaImpl(code);
        consolePrintLog = false;
    }

    [TasCommand(commandName, LegalInMainGame = false)]
    private static void EvalLua(string[] args, string lineText) {
        if (args.IsEmpty()) {
            return;
        }
        
        EvalLuaImpl(commandAndSeparatorRegex.Replace(lineText, ""));
    }

    private static void EvalLuaImpl(string code) {
        ModAsset modAsset = Everest.Content.Get("env", true);
        using StreamReader streamReader = new(modAsset.Stream);
        string envCode = streamReader.ReadToEnd();

        code = $"{envCode}\n{code}";

        object[] objects;
        try {
            objects = Everest.LuaLoader.Run(code, null);
        } catch (Exception e) {
            Engine.Commands.Log(e);
            e.Log();
            return;
        }

        LogResult(objects);
    }

    private static void LogResult(object[] objects) {
        if (consolePrintLog) {
            var result = new List<string>();

            if (objects == null || objects.Length == 0) {
                return;
            } else if (objects.Length == 1) {
                result.Add(objects[0]?.ToString() ?? "null");
            } else {
                for (var i = 0; i < objects.Length; i++) {
                    result.Add($"{i + 1}: {objects[i]?.ToString() ?? "null"}");
                }
            }

            Engine.Commands.Log(string.Join("\n", result));
        }
    }

    private static void Log(string text) {
        if (consolePrintLog) {
            Engine.Commands.Log(text);
        }

        $"EvalLua Command Failed: {text}".Log();
    }
}