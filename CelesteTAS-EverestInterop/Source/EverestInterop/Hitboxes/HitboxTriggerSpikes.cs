﻿using System;
using System.Reflection;
using Celeste;
using Celeste.Mod.Entities;
using Microsoft.Xna.Framework;
using Monocle;
using TAS.Module;
using TAS.Utils;

namespace TAS.EverestInterop.Hitboxes;

public static class HitboxTriggerSpikes {
    private static readonly GetDelegate<TriggerSpikes, TriggerSpikes.Directions> TriggerSpikesDirection =
        FastReflection.CreateGetDelegate<TriggerSpikes, TriggerSpikes.Directions>("direction");

    private static readonly GetDelegate<object, Array> TriggerSpikesSpikes = typeof(TriggerSpikes).CreateGetDelegate<object, Array>("spikes");

    private static readonly Type spikeInfoType = typeof(TriggerSpikes).GetNestedType("SpikeInfo", BindingFlags.NonPublic);
    private static readonly GetDelegate<object, bool> triggerSpikesTriggered = spikeInfoType.CreateGetDelegate<object, bool>("Triggered");
    private static readonly GetDelegate<object, float> triggerSpikesLerp = spikeInfoType.CreateGetDelegate<object, float>("Lerp");

    private static readonly GetDelegate<TriggerSpikesOriginal, TriggerSpikesOriginal.Directions> TriggerSpikesOriginalDirection =
        FastReflection.CreateGetDelegate<TriggerSpikesOriginal, TriggerSpikesOriginal.Directions>("direction");

    private static readonly GetDelegate<object, Array> TriggerSpikesOriginalSpikes =
        typeof(TriggerSpikesOriginal).CreateGetDelegate<object, Array>("spikes");

    private static readonly Type originalSpikeInfoType = typeof(TriggerSpikesOriginal).GetNestedType("SpikeInfo", BindingFlags.NonPublic);

    private static readonly GetDelegate<object, bool> triggerSpikesOriginalTriggered =
        originalSpikeInfoType.CreateGetDelegate<object, bool>("Triggered");

    private static readonly GetDelegate<object, float> triggerSpikesOriginalLerp = originalSpikeInfoType.CreateGetDelegate<object, float>("Lerp");

    private static readonly GetDelegate<object, Vector2> triggerSpikesOriginalPosition =
        originalSpikeInfoType.CreateGetDelegate<object, Vector2>("Position");

    private static Type groupedTriggerSpikesType;
    private static GetDelegate<object, bool> groupedTriggerSpikesTriggered;
    private static GetDelegate<object, float> groupedTriggerSpikesLerp;

    static HitboxTriggerSpikes() { }

    [Initialize]
    private static void Initialize() {
        groupedTriggerSpikesType = ModUtils.GetType("MaxHelpingHand", "Celeste.Mod.MaxHelpingHand.Entities.GroupedTriggerSpikes");
        groupedTriggerSpikesTriggered = groupedTriggerSpikesType?.CreateGetDelegate<object, bool>("Triggered");
        groupedTriggerSpikesLerp = groupedTriggerSpikesType?.CreateGetDelegate<object, float>("Lerp");
        if (groupedTriggerSpikesType != null && groupedTriggerSpikesTriggered != null && groupedTriggerSpikesLerp != null) {
            On.Monocle.Entity.DebugRender += ShowGroupedTriggerSpikesHitboxes;
        }
    }

    [Load]
    private static void Load() {
        On.Monocle.Entity.DebugRender += ShowTriggerSpikesHitboxes;
    }

    [Unload]
    private static void Unload() {
        On.Monocle.Entity.DebugRender -= ShowGroupedTriggerSpikesHitboxes;
        On.Monocle.Entity.DebugRender -= ShowTriggerSpikesHitboxes;
    }

    private static void ShowGroupedTriggerSpikesHitboxes(On.Monocle.Entity.orig_DebugRender orig, Entity self, Camera camera) {
        if (!TasSettings.ShowHitboxes || self.GetType() != groupedTriggerSpikesType) {
            orig(self, camera);
            return;
        }

        self.Collider.Render(camera, HitboxColor.EntityColorInverselyLessAlpha);
        if (groupedTriggerSpikesTriggered(self) && groupedTriggerSpikesLerp(self) >= 1f) {
            self.Collider.Render(camera, HitboxColor.EntityColor);
        }
    }

    private static void ShowTriggerSpikesHitboxes(On.Monocle.Entity.orig_DebugRender orig, Entity self, Camera camera) {
        if (!TasSettings.ShowHitboxes || self is not TriggerSpikes && self is not TriggerSpikesOriginal) {
            orig(self, camera);
            return;
        }

        if (self is TriggerSpikes triggerSpikes) {
            DrawSpikesHitboxes(triggerSpikes, camera);
        } else if (self is TriggerSpikesOriginal triggerSpikesOriginal) {
            DrawSpikesHitboxes(triggerSpikesOriginal, camera);
        }
    }

    private static void DrawSpikesHitboxes(TriggerSpikes triggerSpikes, Camera camera) {
        triggerSpikes.Collider?.Render(camera, HitboxColor.EntityColorInverselyLessAlpha);

        Vector2 offset, value;
        bool vertical = false;
        switch (TriggerSpikesDirection(triggerSpikes)) {
            case TriggerSpikes.Directions.Up:
                offset = new Vector2(-2f, -4f);
                value = new Vector2(1f, 0f);
                break;
            case TriggerSpikes.Directions.Down:
                offset = new Vector2(-2f, 0f);
                value = new Vector2(1f, 0f);
                break;
            case TriggerSpikes.Directions.Left:
                offset = new Vector2(-4f, -2f);
                value = new Vector2(0f, 1f);
                vertical = true;
                break;
            case TriggerSpikes.Directions.Right:
                offset = new Vector2(0f, -2f);
                value = new Vector2(0f, 1f);
                vertical = true;
                break;
            default:
                return;
        }

        Array spikes = TriggerSpikesSpikes(triggerSpikes);
        for (int i = 0; i < spikes.Length; i++) {
            object spikeInfo = spikes.GetValue(i);
            if (triggerSpikesTriggered(spikeInfo) && triggerSpikesLerp(spikeInfo) >= 1f) {
                Vector2 position = triggerSpikes.Position + value * (2 + i * 4) + offset;

                bool startFromZero = i == 0;
                int num = 1;
                for (int j = i + 1; j < spikes.Length; j++) {
                    object nextSpikeInfo = spikes.GetValue(j);
                    if (triggerSpikesTriggered(nextSpikeInfo) && triggerSpikesLerp(nextSpikeInfo) >= 1f) {
                        num++;
                        i++;
                    } else {
                        break;
                    }
                }

                float totalWidth = 4f * (vertical ? 1 : num);
                float totalHeight = 4f * (vertical ? num : 1);
                if (!startFromZero) {
                    if (vertical) {
                        position.Y -= 1;
                        totalHeight += 1;
                    } else {
                        position.X -= 1;
                        totalWidth += 1;
                    }
                }

                Draw.HollowRect(position, totalWidth, totalHeight, HitboxColor.GetCustomColor(triggerSpikes));
            }
        }
    }

    private static void DrawSpikesHitboxes(TriggerSpikesOriginal triggerSpikes, Camera camera) {
        triggerSpikes.Collider?.Render(camera, HitboxColor.EntityColorInverselyLessAlpha);

        Vector2 offset;
        float width, height;
        bool vertical = false;
        switch (TriggerSpikesOriginalDirection(triggerSpikes)) {
            case TriggerSpikesOriginal.Directions.Up:
                width = 8f;
                height = 3f;
                offset = new Vector2(-4f, -4f);
                break;
            case TriggerSpikesOriginal.Directions.Down:
                width = 8f;
                height = 3f;
                offset = new Vector2(-4f, 1f);
                break;
            case TriggerSpikesOriginal.Directions.Left:
                width = 3f;
                height = 8f;
                offset = new Vector2(-4f, -4f);
                vertical = true;
                break;
            case TriggerSpikesOriginal.Directions.Right:
                width = 3f;
                height = 8f;
                offset = new Vector2(1f, -4f);
                vertical = true;
                break;
            default:
                return;
        }

        Array spikes = TriggerSpikesOriginalSpikes(triggerSpikes);
        for (int i = 0; i < spikes.Length; i++) {
            object spikeInfo = spikes.GetValue(i);

            if (triggerSpikesOriginalTriggered(spikeInfo) && triggerSpikesOriginalLerp(spikeInfo) >= 1) {
                bool startFromZero = i == 0;
                int num = 1;
                for (int j = i + 1; j < spikes.Length; j++) {
                    object nextSpikeInfo = spikes.GetValue(j);
                    if (triggerSpikesOriginalTriggered(nextSpikeInfo) && triggerSpikesOriginalLerp(nextSpikeInfo) >= 1) {
                        num++;
                        i++;
                    } else {
                        break;
                    }
                }

                Vector2 position = triggerSpikesOriginalPosition(spikeInfo) + triggerSpikes.Position + offset;
                float totalWidth = width * (vertical ? 1 : num);
                float totalHeight = height * (vertical ? num : 1);
                if (!startFromZero) {
                    if (vertical) {
                        position.Y -= 1;
                        totalHeight += 1;
                    } else {
                        position.X -= 1;
                        totalWidth += 1;
                    }
                }

                Draw.HollowRect(position, totalWidth, totalHeight, HitboxColor.GetCustomColor(triggerSpikes));
            }
        }
    }
}