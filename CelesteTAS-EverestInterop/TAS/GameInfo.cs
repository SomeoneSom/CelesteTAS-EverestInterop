using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Celeste;
using Celeste.Mod;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using TAS.EverestInterop;
using TAS.EverestInterop.InfoHUD;
using TAS.Input;
using TAS.Utils;

namespace TAS {
    public static class GameInfo {
        private static readonly FieldInfo SummitVignetteReadyFieldInfo = typeof(SummitVignette).GetFieldInfo("ready");
        private static readonly MethodInfo EntityListFindAll = typeof(EntityList).GetMethod("FindAll");

        private static readonly DWallJumpCheck WallJumpCheck;
        private static readonly GetBerryFloat StrawberryCollectTimer;
        private static readonly GetFloat DashCooldownTimer;
        private static readonly GetFloat JumpGraceTimer;
        private static readonly GetPlayerSeekerSpeed PlayerSeekerSpeed;
        private static readonly GetPlayerSeekerDashTimer PlayerSeekerDashTimer;
        private static readonly Func<Player, Vector2> PlayerLiftBoost;
        private static readonly GetFloat ActorLiftSpeedTimer;
        private static readonly GetFloat PlayerRetainedSpeed;
        private static readonly GetFloat PlayerRetainedSpeedTimer;
        private static readonly Func<Level, float> LevelUnpauseTimer;
        private static readonly Func<StateMachine, Coroutine> StateMachineCurrentCoroutine;
        private static readonly Func<Coroutine, float> CoroutineWaitTimer;

        private static ILHook dashCoroutineIlHook;

        public static string Status = string.Empty;
        public static string StatusWithoutTime = string.Empty;
        public static string LastVel = string.Empty;
        public static string CustomInfo = string.Empty;
        public static long LastChapterTime;
        public static Vector2Double LastPos;
        public static Vector2Double LastPlayerSeekerPos;
        public static float DashTime;

        private static StreamWriter sw;
        private static IDictionary<string, Func<Level, IList>> trackedEntities;

        private static int transitionFrames;

        //for debugging
        public static string AdditionalStatusInfo;

        static GameInfo() {
            MethodInfo wallJumpCheck = typeof(Player).GetMethodInfo("WallJumpCheck");
            FieldInfo strawberryCollectTimer = typeof(Strawberry).GetFieldInfo("collectTimer");
            FieldInfo dashCooldownTimer = typeof(Player).GetFieldInfo("dashCooldownTimer");
            FieldInfo jumpGraceTimer = typeof(Player).GetFieldInfo("jumpGraceTimer");
            FieldInfo playerSeekerSpeed = typeof(PlayerSeeker).GetFieldInfo("speed");
            FieldInfo playerSeekerDashTimer = typeof(PlayerSeeker).GetFieldInfo("dashTimer");
            MethodInfo playerLiftSpeed = typeof(Player).GetPropertyInfo("LiftBoost").GetGetMethod(true);
            FieldInfo actorLiftSpeedTimer = typeof(Actor).GetFieldInfo("liftSpeedTimer");
            FieldInfo playerRetainedSpeed = typeof(Player).GetFieldInfo("wallSpeedRetained");
            FieldInfo playerRetainedSpeedTimer = typeof(Player).GetFieldInfo("wallSpeedRetentionTimer");
            FieldInfo levelUnpauseTimer = typeof(Level).GetFieldInfo("unpauseTimer");
            FieldInfo currentCoroutine = typeof(StateMachine).GetFieldInfo("currentCoroutine");
            FieldInfo waitTimer = typeof(Coroutine).GetFieldInfo("waitTimer");

            WallJumpCheck = (DWallJumpCheck) wallJumpCheck.CreateDelegate(typeof(DWallJumpCheck));
            StrawberryCollectTimer = strawberryCollectTimer.CreateDelegate_Get<GetBerryFloat>();
            DashCooldownTimer = dashCooldownTimer.CreateDelegate_Get<GetFloat>();
            JumpGraceTimer = jumpGraceTimer.CreateDelegate_Get<GetFloat>();
            PlayerSeekerSpeed = playerSeekerSpeed.CreateDelegate_Get<GetPlayerSeekerSpeed>();
            PlayerSeekerDashTimer = playerSeekerDashTimer.CreateDelegate_Get<GetPlayerSeekerDashTimer>();
            PlayerLiftBoost = (Func<Player, Vector2>) playerLiftSpeed.CreateDelegate(typeof(Func<Player, Vector2>));
            ActorLiftSpeedTimer = actorLiftSpeedTimer.CreateDelegate_Get<GetFloat>();
            PlayerRetainedSpeed = playerRetainedSpeed.CreateDelegate_Get<GetFloat>();
            PlayerRetainedSpeedTimer = playerRetainedSpeedTimer.CreateDelegate_Get<GetFloat>();
            LevelUnpauseTimer = levelUnpauseTimer?.CreateDelegate_Get<Func<Level, float>>();
            StateMachineCurrentCoroutine = currentCoroutine.CreateDelegate_Get<Func<StateMachine, Coroutine>>();
            CoroutineWaitTimer = waitTimer.CreateDelegate_Get<Func<Coroutine, float>>();
        }

        private static int FramesPerSecond => (int) Math.Round(1 / Engine.RawDeltaTime);

        public static bool ExportSyncData { get; private set; }

        [Load]
        private static void Load() {
            On.Monocle.Engine.Update += EngineOnUpdate;
            On.Monocle.Scene.AfterUpdate += SceneOnAfterUpdate;
            Everest.Events.Level.OnTransitionTo += LevelOnOnTransitionTo;
            On.Celeste.Level.Update += LevelOnUpdate;
            dashCoroutineIlHook = new ILHook(typeof(Player).GetMethodInfo("DashCoroutine").GetStateMachineTarget(), PlayerOnDashCoroutine);
        }

        [Unload]
        private static void Unload() {
            On.Monocle.Engine.Update -= EngineOnUpdate;
            On.Monocle.Scene.AfterUpdate -= SceneOnAfterUpdate;
            Everest.Events.Level.OnTransitionTo -= LevelOnOnTransitionTo;
            On.Celeste.Level.Update -= LevelOnUpdate;
            dashCoroutineIlHook?.Dispose();
            dashCoroutineIlHook = null;
        }

        private static void PlayerOnDashCoroutine(ILContext il) {
            ILCursor ilCursor = new(il);
            while (ilCursor.TryGotoNext(
                ins => ins.OpCode == OpCodes.Ldarg_0,
                ins => ins.OpCode == OpCodes.Ldc_R4,
                ins => ins.MatchBox<float>(),
                ins => ins.OpCode == OpCodes.Stfld && ins.Operand.ToString().EndsWith("::<>2__current")
                )) {
                ilCursor.Index += 2;
                ilCursor.EmitDelegate<Func<float, float>>(dashTime => {
                    DashTime = dashTime;
                    return dashTime;
                });
            }
        }

        private static void EngineOnUpdate(On.Monocle.Engine.orig_Update orig, Engine self, GameTime gameTime) {
            bool frozen = Engine.FreezeTimer > 0;
            orig(self, gameTime);
            if (frozen) {
                Update();
            }
        }

        private static void SceneOnAfterUpdate(On.Monocle.Scene.orig_AfterUpdate orig, Scene self) {
            orig(self);
            Update();
        }

        private static void LevelOnOnTransitionTo(Level level, LevelData next, Vector2 direction) {
            transitionFrames = GetTransitionFrames(level, next);
        }

        private static void LevelOnUpdate(On.Celeste.Level.orig_Update orig, Level self) {
            orig(self);

            if (transitionFrames > 0) {
                transitionFrames--;
            }
        }

        private static int GetTransitionFrames(Level level, LevelData nextLevelData) {
            int result = 0;
            Session session = level.Session;

            bool darkRoom = nextLevelData.Dark && !session.GetFlag("ignore_darkness_" + nextLevelData.Name);

            float lightingStart = level.Lighting.Alpha;
            float lightingCurrent = lightingStart;
            float lightingEnd = darkRoom ? session.DarkRoomAlpha : level.BaseLightingAlpha + session.LightingAlphaAdd;
            bool lightingWait = lightingStart >= session.DarkRoomAlpha || lightingEnd >= session.DarkRoomAlpha;
            if (lightingWait) {
                while (Math.Abs(lightingCurrent - lightingEnd) > 0.000001f) {
                    result++;
                    lightingCurrent = Calc.Approach(lightingCurrent, lightingEnd, 2f * Engine.DeltaTime);
                }
            }

            result += level.NextTransitionDuration.ToCeilingFrames() + 1;

            return result;
        }

        public static void Update() {
            if (Engine.Scene is Level level) {
                Player player = level.Tracker.GetEntity<Player>();
                long chapterTime = level.Session.Time;
                if (player != null) {
                    StringBuilder stringBuilder = new();
                    string pos = GetAdjustedPos(player.Position, player.PositionRemainder);
                    string speed = GetAdjustedSpeed(player.Speed);
                    Vector2Double diff = (player.GetMoreExactPosition() - LastPos) * FramesPerSecond;
                    string velocity = GetAdjustedVelocity(diff);
                    if (chapterTime == LastChapterTime) {
                        velocity = LastVel;
                    } else {
                        LastVel = velocity;
                    }

                    string polarVel = $"Fly:   {diff.Length():F2}, {diff.Angle():F5}°";

                    string analog = string.Empty;
                    if (Manager.Running && Manager.Controller.Previous is { } inputFrame && inputFrame.HasActions(Actions.Feather)) {
                        Vector2 angleVector2 = inputFrame.AngleVector2;
                        analog =
                            $"Analog: {angleVector2.X:F5}, {angleVector2.Y:F5}, {Manager.GetAngle(new Vector2(angleVector2.X, -angleVector2.Y)):F5}°";
                    }

                    string retainedSpeed = string.Empty;
                    if (PlayerRetainedSpeedTimer(player) is float retainedSpeedTimer and > 0f) {
                        retainedSpeed = $"Retained: {PlayerRetainedSpeed(player):F2} ({retainedSpeedTimer.ToCeilingFrames()})";
                    }

                    string liftBoost = string.Empty;
                    if (PlayerLiftBoost(player) is var liftBoostVector2 && liftBoostVector2 != Vector2.Zero) {
                        liftBoost =
                            $"LiftBoost: {liftBoostVector2.X:F2}, {liftBoostVector2.Y:F2} ({ActorLiftSpeedTimer(player).ToCeilingFrames()})";
                    }

                    string miscStats = $"Stamina: {player.Stamina:0} "
                                       + (WallJumpCheck(player, 1) ? "Wall-R " : string.Empty)
                                       + (WallJumpCheck(player, -1) ? "Wall-L " : string.Empty)
                                       + PlayerStates.GetStateName(player.StateMachine.State);

                    int dashCooldown = DashCooldownTimer(player).ToFloorFrames();

                    PlayerSeeker playerSeeker = level.Tracker.GetEntity<PlayerSeeker>();
                    if (playerSeeker != null) {
                        pos = GetAdjustedPos(playerSeeker.Position, playerSeeker.PositionRemainder);
                        speed = GetAdjustedSpeed(PlayerSeekerSpeed(playerSeeker));
                        diff = (playerSeeker.GetMoreExactPosition() - LastPlayerSeekerPos) * FramesPerSecond;
                        velocity = GetAdjustedVelocity(diff);
                        polarVel = $"Chase: {diff.Length():F2}, {diff.Angle():F5}°";
                        dashCooldown = PlayerSeekerDashTimer(playerSeeker).ToCeilingFrames();
                    }

                    string statuses = (dashCooldown <= 0 && player.Dashes > 0 ? "CanDash " : string.Empty)
                                      + (player.LoseShards ? "Ground " : string.Empty)
                                      + (!player.LoseShards && JumpGraceTimer(player).ToFloorFrames() is int coyote and > 0
                                          ? $"Coyote({coyote}) "
                                          : string.Empty);

                    string noControlFrames = transitionFrames > 0 ? $"({transitionFrames})" : string.Empty;
                    float unpauseTimer = LevelUnpauseTimer?.Invoke(level) ?? 0f;
                    if (unpauseTimer > 0f) {
                        noControlFrames = $"({unpauseTimer.ToCeilingFrames()})";
                    }

                    statuses = (Engine.FreezeTimer > 0f ? $"Frozen({Engine.FreezeTimer.ToCeilingFrames()}) " : string.Empty)
                               + (player.InControl && !level.Transitioning && unpauseTimer <= 0f ? statuses : $"NoControl{noControlFrames} ")
                               + (player.Dead ? "Dead " : string.Empty)
                               + (level.InCutscene ? "Cutscene " : string.Empty)
                               + (AdditionalStatusInfo ?? string.Empty);

                    if (player.Holding == null
                        && level.Tracker.GetComponents<Holdable>().Any(holdable => ((Holdable) holdable).Check(player))) {
                        statuses += "Grab ";
                    }

                    string timers = string.Empty;
                    Follower firstRedBerryFollower = player.Leader.Followers.Find(follower => follower.Entity is Strawberry {Golden: false});
                    if (firstRedBerryFollower?.Entity is Strawberry firstRedBerry) {
                        float collectTimer = StrawberryCollectTimer(firstRedBerry);
                        if (collectTimer <= 0.15f) {
                            int collectFrames = (0.15f - collectTimer).ToCeilingFrames();
                            if (collectTimer >= 0f) {
                                timers += $"Berry({collectFrames}) ";
                            } else {
                                int additionalFrames = Math.Abs(collectTimer).ToCeilingFrames();
                                timers += $"Berry({collectFrames - additionalFrames}+{additionalFrames}) ";
                            }
                        }
                    }

                    if (dashCooldown > 0) {
                        timers += $"DashCD({dashCooldown}) ";
                    }

                    if ((FramesPerSecond != 60 || Math.Abs(Engine.TimeRateB - 1f) > 0.000001f || SaveData.Instance.Assists.SuperDashing) && DashTime.ToCeilingFrames() >= 1 && player.StateMachine.State == Player.StDash) {
                        DashTime = CoroutineWaitTimer(StateMachineCurrentCoroutine(player.StateMachine));
                        timers += $"Dash({DashTime.ToCeilingFrames()}) ";
                    }

                    stringBuilder.AppendLine(pos);
                    stringBuilder.AppendLine(speed);
                    stringBuilder.AppendLine(velocity);

                    if (player.StateMachine.State == Player.StStarFly
                        || playerSeeker != null
                        || SaveData.Instance.Assists.ThreeSixtyDashing
                        || SaveData.Instance.Assists.SuperDashing) {
                        stringBuilder.AppendLine(polarVel);
                    }

                    if (!string.IsNullOrEmpty(analog)) {
                        stringBuilder.AppendLine(analog);
                    }

                    if (!string.IsNullOrEmpty(retainedSpeed)) {
                        stringBuilder.AppendLine(retainedSpeed);
                    }

                    if (!string.IsNullOrEmpty(liftBoost)) {
                        stringBuilder.AppendLine(liftBoost);
                    }

                    stringBuilder.AppendLine(miscStats);
                    if (!string.IsNullOrEmpty(statuses)) {
                        stringBuilder.AppendLine(statuses);
                    }

                    if (!string.IsNullOrEmpty(timers)) {
                        stringBuilder.AppendLine(timers);
                    }

                    if (Manager.FrameLoops == 1) {
                        string inspectingInfo = InfoInspectEntity.GetInspectingEntitiesInfo();
                        if (inspectingInfo.IsNotNullOrEmpty()) {
                            stringBuilder.AppendLine(inspectingInfo);
                        }

                        CustomInfo = InfoCustom.Parse();
                        if (CustomInfo.IsNotEmpty()) {
                            stringBuilder.AppendLine(CustomInfo);
                        }
                    }

                    StatusWithoutTime = stringBuilder.ToString();
                    if (Engine.FreezeTimer <= 0f) {
                        LastPos = player.GetMoreExactPosition();
                        LastPlayerSeekerPos = playerSeeker?.GetMoreExactPosition() ?? default;
                    }
                } else if (level.InCutscene) {
                    StatusWithoutTime = "Cutscene";
                }

                string roomNameAndTime =
                    $"[{level.Session.Level}] Timer: {(chapterTime / 10000000D):F3}({chapterTime / TimeSpan.FromSeconds(Engine.RawDeltaTime).Ticks})";
                Status = StatusWithoutTime + roomNameAndTime;
                LastChapterTime = chapterTime;
            } else if (Engine.Scene is SummitVignette summit) {
                Status = "SummitVignette " + SummitVignetteReadyFieldInfo.GetValue(summit);
            } else if (Engine.Scene is Overworld overworld) {
                Status = "Overworld " + overworld.ShowInputUI;
            } else if (Engine.Scene != null) {
                Status = Engine.Scene.GetType().Name;
            }
        }

        private static int ToCeilingFrames(this float seconds) {
            return (int) Math.Ceiling(seconds / Engine.RawDeltaTime / Engine.TimeRateB);
        }

        private static int ToFloorFrames(this float seconds) {
            return (int) Math.Floor(seconds / Engine.RawDeltaTime / Engine.TimeRateB);
        }

        public static int ConvertToFrames(float seconds) {
            return seconds.ToCeilingFrames();
        }

        private static string GetAdjustedPos(Vector2 intPos, Vector2 subpixelPos) {
            double x = intPos.X;
            double y = intPos.Y;
            double subX = subpixelPos.X;
            double subY = subpixelPos.Y;

            if (!CelesteTasModule.Settings.RoundPosition) {
                return $"Pos:   {x + subX:F12}, {y + subY:F12}";
            }

            if (Math.Abs(subX) % 0.25 < 0.01 || Math.Abs(subX) % 0.25 > 0.24) {
                if (x > 0 || x == 0 && subX > 0) {
                    x += Math.Floor(subX * 100) / 100;
                } else {
                    x += Math.Ceiling(subX * 100) / 100;
                }
            } else {
                x += subX;
            }

            if (Math.Abs(subY) % 0.25 < 0.01 || Math.Abs(subY) % 0.25 > 0.24) {
                if (y > 0 || y == 0 && subY > 0) {
                    y += Math.Floor(subY * 100) / 100;
                } else {
                    y += Math.Ceiling(subY * 100) / 100;
                }
            } else {
                y += subY;
            }

            string pos = $"Pos:   {x:F2}, {y:F2}";
            return pos;
        }

        private static string GetAdjustedSpeed(Vector2 speed) {
            return $"Speed: {speed.ToSimpleString(CelesteTasModule.Settings.RoundSpeed)}";
        }

        private static string GetAdjustedVelocity(Vector2Double diff) {
            return $"Vel:   {diff.ToSimpleString(CelesteTasModule.Settings.RoundVelocity)}";
        }

        private static void BeginExport(string path, string[] tracked) {
            sw?.Dispose();
            sw = new StreamWriter(path);
            sw.WriteLine(string.Join("\t", "Line", "Inputs", "Frames", "Time", "Position", "Speed", "State", "Statuses", "Entities"));
            trackedEntities = new Dictionary<string, Func<Level, IList>>();
            foreach (string typeName in tracked) {
                string fullTypeName = typeName.Contains("@") ? typeName.Replace("@", ",") : $"Celeste.{typeName}, Celeste";
                Type t = Type.GetType(fullTypeName);
                if (t != null && t.IsSameOrSubclassOf(typeof(Entity))) {
                    trackedEntities[t.Name] = level => FindEntity(t, level);
                }
            }
        }

        private static IList FindEntity(Type type, Level level) {
            if (level.Tracker.Entities.ContainsKey(type)) {
                return level.Tracker.Entities[type];
            } else {
                return EntityListFindAll.MakeGenericMethod(type).Invoke(level.Entities, null) as IList;
            }
        }

        public static void EndExport() {
            ExportSyncData = false;
            Engine.Scene.OnEndOfFrame += () => { sw?.Dispose(); };
        }

        public static void ExportPlayerInfo() {
            Engine.Scene.OnEndOfFrame += () => {
                InputController controller = Manager.Controller;
                if (Engine.Scene is Level level) {
                    Player player = level.Tracker.GetEntity<Player>();
                    if (player == null) {
                        return;
                    }

                    string time = (level.Session.Time / 10000000D).ToString("0.000");
                    string pos = player.ToSimplePositionString(CelesteTasModule.Settings.RoundPosition);
                    string speed = player.Speed.X + ", " + player.Speed.Y;

                    int dashCooldown = (int) (DashCooldownTimer(player));
                    string statuses = (dashCooldown < 1 && player.Dashes > 0 ? "Dash " : string.Empty)
                                      + (player.LoseShards ? "Ground " : string.Empty)
                                      + (WallJumpCheck(player, 1) ? "Wall-R " : string.Empty)
                                      + (WallJumpCheck(player, -1) ? "Wall-L " : string.Empty)
                                      + (!player.LoseShards && JumpGraceTimer(player) > 0 ? "Coyote " : string.Empty);
                    statuses = (player.InControl && !level.Transitioning ? statuses : "NoControl ")
                               + (player.TimePaused ? "Paused " : string.Empty)
                               + (level.InCutscene ? "Cutscene " : string.Empty)
                               + (AdditionalStatusInfo ?? string.Empty);

                    if (player.Holding == null) {
                        foreach (Component component in level.Tracker.GetComponents<Holdable>()) {
                            Holdable holdable = (Holdable) component;
                            if (holdable.Check(player)) {
                                statuses += "Grab ";
                                break;
                            }
                        }
                    }

                    string output = string.Empty;
                    if (controller.CurrentFrame > 0 && controller.Inputs.Count > 0) {
                        output = string.Join("\t",
                            controller.Previous.Line, controller.Previous, controller.CurrentFrame - 1, time, pos, speed,
                            PlayerStates.GetStateName(player.StateMachine.State),
                            statuses);
                    }

                    foreach (string typeName in trackedEntities.Keys) {
                        IList entities = trackedEntities[typeName].Invoke(level);
                        if (entities == null) {
                            continue;
                        }

                        foreach (Entity entity in entities) {
                            output += $"\t{typeName}: {entity.ToSimplePositionString(CelesteTasModule.Settings.RoundCustomInfo)}";
                        }
                    }

                    output += $"\t{InfoInspectEntity.GetInspectingEntitiesInfo("\t")}";

                    sw.WriteLine(output);
                } else {
                    string inputs = controller.Current.ToString();
                    if (inputs.Length > 1) {
                        inputs = inputs.Substring(1);
                    }

                    string output = string.Join(" ", inputs, controller.CurrentFrame, Engine.Scene.GetType().Name);
                    sw.WriteLine(output);
                }
            };
        }

        // ReSharper disable once UnusedMember.Local
        // "StartExport",
        // "StartExport Path",
        // "StartExport EntitiesToTrack",
        // "StartExport Path EntitiesToTrack"
        [TasCommand(Name = "StartExport")]
        private static void StartExportCommand(string[] args) {
            string path = "dump.txt";
            if (args.Length > 0) {
                if (args[0].Contains(".")) {
                    path = args[0];
                    args = args.Skip(1).ToArray();
                }
            }

            BeginExport(path, args);
            ExportSyncData = true;
        }

        // ReSharper disable once UnusedMember.Local
        [TasCommand(Name = "FinishExport")]
        private static void FinishExportCommand(string[] args) {
            EndExport();
            ExportSyncData = false;
        }

        //The things we do for faster replay times
        private delegate bool DWallJumpCheck(Player player, int dir);

        private delegate float GetBerryFloat(Strawberry berry);

        private delegate float GetFloat(Player player);

        private delegate Vector2 GetPlayerSeekerSpeed(PlayerSeeker playerSeeker);

        private delegate float GetPlayerSeekerDashTimer(PlayerSeeker playerSeeker);
    }

    public static class PlayerStates {
        private static readonly IDictionary<int, string> States = new Dictionary<int, string> {
            {Player.StNormal, "StNormal"},
            {Player.StClimb, "StClimb"},
            {Player.StDash, "StDash"},
            {Player.StSwim, "StSwim"},
            {Player.StBoost, "StBoost"},
            {Player.StRedDash, "StRedDash"},
            {Player.StHitSquash, "StHitSquash"},
            {Player.StLaunch, "StLaunch"},
            {Player.StPickup, "StPickup"},
            {Player.StDreamDash, "StDreamDash"},
            {Player.StSummitLaunch, "StSummitLaunch"},
            {Player.StDummy, "StDummy"},
            {Player.StIntroWalk, "StIntroWalk"},
            {Player.StIntroJump, "StIntroJump"},
            {Player.StIntroRespawn, "StIntroRespawn"},
            {Player.StIntroWakeUp, "StIntroWakeUp"},
            {Player.StBirdDashTutorial, "StBirdDashTutorial"},
            {Player.StFrozen, "StFrozen"},
            {Player.StReflectionFall, "StReflectionFall"},
            {Player.StStarFly, "StStarFly"},
            {Player.StTempleFall, "StTempleFall"},
            {Player.StCassetteFly, "StCassetteFly"},
            {Player.StAttract, "StAttract"},
            {Player.StIntroMoonJump, "StIntroMoonJump"},
            {Player.StFlingBird, "StFlingBird"},
            {Player.StIntroThinkForABit, "StIntroThinkForABit"},
        };

        public static string GetStateName(int state) {
            return States.ContainsKey(state) ? States[state] : state.ToString();
        }

        // ReSharper disable once UnusedMember.Global
        public static void Register(int state, string stateName) {
            States[state] = stateName;
        }

        // ReSharper disable once UnusedMember.Global
        public static bool Unregister(int state) {
            return States.Remove(state);
        }
    }

    // ReSharper disable once StructCanBeMadeReadOnly
    public struct Vector2Double {
        public readonly double X;
        public readonly double Y;

        public Vector2Double(double x = 0, double y = 0) {
            X = x;
            Y = y;
        }

        public override bool Equals(object obj) => obj is Vector2Double other && X == other.X && Y == other.Y;

        public override int GetHashCode() => ToString().GetHashCode();

        public override string ToString() => "{X:" + X + " Y:" + Y + "}";

        public string ToSimpleString(bool round) => $"{X.ToString(round ? "F2" : "F12")}, {Y.ToString(round ? "F2" : "F12")}";

        public double Length() => Math.Sqrt(X * X + Y * Y);

        public double Angle() {
            double angle = 180 / Math.PI * Math.Atan2(Y, X);
            if (angle < -90.01) {
                return 450 + angle;
            } else {
                return 90 + angle;
            }
        }

        public static bool operator ==(Vector2Double value1, Vector2Double value2) => value1.X == value2.X && value1.Y == value2.Y;

        public static bool operator !=(Vector2Double value1, Vector2Double value2) => !(value1 == value2);

        public static Vector2Double operator +(Vector2Double value1, Vector2Double value2) {
            return new(value1.X + value2.X, value1.Y + value2.Y);
        }

        public static Vector2Double operator -(Vector2Double value1, Vector2Double value2) {
            return new(value1.X - value2.X, value1.Y - value2.Y);
        }

        public static Vector2Double operator *(Vector2Double value, double scaleFactor) {
            return new(value.X * scaleFactor, value.Y * scaleFactor);
        }

        public static Vector2Double operator /(Vector2Double value, double scaleFactor) {
            return value * (1 / scaleFactor);
        }
    }

    internal static class Vector2DoubleExtension {
        public static Vector2Double GetMoreExactPosition(this Actor actor) {
            return new(actor.Position.X + actor.PositionRemainder.X, actor.Position.Y + actor.PositionRemainder.Y);
        }

        public static string ToSimplePositionString(this Entity entity, bool round) {
            if (entity is Actor actor) {
                return actor.GetMoreExactPosition().ToSimpleString(round);
            } else {
                return entity.Position.ToSimpleString(round);
            }
        }
    }
}