﻿using System;
using Celeste;
using Celeste.Mod.UI;
using Microsoft.Xna.Framework;
using Monocle;
using TAS.Module;
using TAS.Utils;

namespace TAS.EverestInterop {
    public static class CenterCamera {
        private static Vector2? savedCameraPosition;
        private static Vector2? lastPlayerPosition;
        private static Vector2 offset;
        private static CelesteTasModuleSettings Settings => CelesteTasModule.Settings;

        public static void Load() {
            // note: change the camera.position before level.BeforeRender will cause desync 3A-roof04
            On.Celeste.Level.Render += LevelOnRender;
            On.Celeste.DustEdges.BeforeRender += DustEdgesOnBeforeRender;
            On.Celeste.MirrorSurfaces.BeforeRender += MirrorSurfacesOnBeforeRender;
            On.Celeste.LightingRenderer.BeforeRender += LightingRendererOnRender;
            On.Celeste.DisplacementRenderer.BeforeRender += DisplacementRendererOnBeforeRender;
            On.Celeste.HudRenderer.RenderContent += HudRendererOnRenderContent;
            On.Celeste.Mod.UI.SubHudRenderer.RenderContent += SubHudRendererOnRenderContent;
            On.Monocle.Commands.Render += CommandsOnRender;
        }

        public static void Unload() {
            On.Celeste.Level.Render -= LevelOnRender;
            On.Celeste.DustEdges.BeforeRender -= DustEdgesOnBeforeRender;
            On.Celeste.MirrorSurfaces.BeforeRender -= MirrorSurfacesOnBeforeRender;
            On.Celeste.LightingRenderer.BeforeRender -= LightingRendererOnRender;
            On.Celeste.DisplacementRenderer.BeforeRender -= DisplacementRendererOnBeforeRender;
            On.Celeste.HudRenderer.RenderContent -= HudRendererOnRenderContent;
            On.Celeste.Mod.UI.SubHudRenderer.RenderContent -= SubHudRendererOnRenderContent;
            On.Monocle.Commands.Render -= CommandsOnRender;
        }

        private static void CenterTheCamera(Action action) {
            Camera camera = (Engine.Scene as Level)?.Camera;
            if (Settings.CenterCamera && camera != null) {
                if (Engine.Scene.GetPlayer() is { } player) {
                    lastPlayerPosition = player.Position;
                }

                if (lastPlayerPosition != null) {
                    savedCameraPosition = camera.Position;
                    camera.Position = lastPlayerPosition.Value + offset - new Vector2(camera.Viewport.Width / 2f, camera.Viewport.Height / 2f);
                }
            }

            action();

            if (savedCameraPosition != null && camera != null) {
                camera.Position = savedCameraPosition.Value;
                savedCameraPosition = null;
            }
        }

        private static void LevelOnRender(On.Celeste.Level.orig_Render orig, Level self) {
            CenterTheCamera(() => orig(self));
            if (Settings.CenterCamera) {
                if (MouseButtons.Middle.LastCheck && MouseButtons.Middle.Check) {
                    offset -= MouseButtons.Position - MouseButtons.LastPosition;
                }

                if (MouseButtons.Middle.DoublePressed) {
                    offset = Vector2.Zero;
                }
            }
        }

        private static void DustEdgesOnBeforeRender(On.Celeste.DustEdges.orig_BeforeRender orig, DustEdges self) {
            CenterTheCamera(() => orig(self));
        }

        private static void MirrorSurfacesOnBeforeRender(On.Celeste.MirrorSurfaces.orig_BeforeRender orig, MirrorSurfaces self) {
            CenterTheCamera(() => orig(self));
        }

        private static void LightingRendererOnRender(On.Celeste.LightingRenderer.orig_BeforeRender orig, LightingRenderer self, Scene scene) {
            CenterTheCamera(() => orig(self, scene));
        }

        private static void DisplacementRendererOnBeforeRender(On.Celeste.DisplacementRenderer.orig_BeforeRender orig, DisplacementRenderer self,
            Scene scene) {
            CenterTheCamera(() => orig(self, scene));
        }

        private static void SubHudRendererOnRenderContent(On.Celeste.Mod.UI.SubHudRenderer.orig_RenderContent orig, SubHudRenderer self,
            Scene scene) {
            CenterTheCamera(() => orig(self, scene));
        }

        private static void HudRendererOnRenderContent(On.Celeste.HudRenderer.orig_RenderContent orig, HudRenderer self, Scene scene) {
            CenterTheCamera(() => orig(self, scene));
        }

        private static void CommandsOnRender(On.Monocle.Commands.orig_Render orig, Monocle.Commands self) {
            CenterTheCamera(() => orig(self));
        }
    }
}