using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    public class MeshStackBuildResult
    {
        public List<string> primaryBlendshapeNames = new List<string>();
        public int rendererCount;
        public int layerCount;
    }

    public static class MeshStackService
    {
        private const string StackFolderName = "MeshStacks";
        private const string GeneratedFolderName = "Generated";

        private class StackRecord
        {
            public MeshStackAsset stack;
            public string assetPath;
            public bool isNew;
            public StackSnapshot snapshot;
        }

        private class StackSnapshot
        {
            public Mesh baseMesh;
            public string baseMeshPath;
            public string baseMeshGuid;
            public Mesh composedMesh;
            public string composedMeshPath;
            public string composedMeshGuid;
            public List<MeshStackLayer> layers;
        }

        private class Participation
        {
            public string rendererPath;
            public MeshStackLayerRole role;
            public TrackedMesh trackedMesh;
        }

        private class LayerRef
        {
            public StackRecord record;
            public MeshStackLayer layer;
        }

        private class LayerGroup
        {
            public string stableConfigId;
            public string configPath;
            public int order;
            public List<LayerRef> refs = new List<LayerRef>();
        }

        public static MeshStackBuildResult GenerateOrUpdateBulge(
            BulgeConfig config, string configAssetPath)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (config.avatarRoot == null)
                throw new InvalidOperationException("Avatar root is not set.");

            config.EnsureStableConfigId();

            var records = LoadStackRecords(config.avatarRoot);
            try
            {
                RegisterAssignedLegacyBulgeGenerations(
                    config.avatarRoot, records, config.stableConfigId);

                int order = FindExistingOrder(records, config.stableConfigId);
                if (order <= 0)
                    order = NextLayerOrder(records);

                RemoveConfigLayers(records, config.stableConfigId, "Bulge");

                var participants = GetBulgeParticipants(config);
                if (participants.Count == 0)
                    throw new InvalidOperationException(
                        "No valid target renderers were found for this configuration.");

                bool addedLayer = false;
                foreach (var participant in participants)
                {
                    var renderer = BaseEffectConfig.ResolveRenderer(
                        config.avatarRoot, participant.rendererPath);
                    if (renderer == null || renderer.sharedMesh == null)
                    {
                        Debug.LogWarning(
                            $"[SPS Mesh Stack] Renderer '{participant.rendererPath}' " +
                            "could not be resolved and was skipped.");
                        continue;
                    }

                    var record = GetOrCreateStackRecord(
                        records, config.avatarRoot, participant.rendererPath);
                    EnsureBaseMesh(record.stack, renderer,
                        ResolveLegacyOriginal(config, participant));

                    record.stack.layers.Add(new MeshStackLayer
                    {
                        stableConfigId = config.stableConfigId,
                        configPath = configAssetPath,
                        effectType = config.EffectTypeName,
                        configurationName = config.configurationName,
                        rendererPath = participant.rendererPath,
                        role = participant.role,
                        order = order,
                        isEnabled = true
                    });
                    addedLayer = true;
                }
                if (!addedLayer)
                    throw new InvalidOperationException(
                        "No target renderers could be resolved for this configuration.");

                ValidateBulgeNameCollisions(config, records);
                var result = RebuildBulgeStacks(config.avatarRoot, records, config);
                UpdateLegacyReferences(config, records);
                return result;
            }
            catch
            {
                RestoreSnapshots(records);
                throw;
            }
        }

        public static MeshStackBuildResult RemoveBulgeConfig(BulgeConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (config.avatarRoot == null || string.IsNullOrEmpty(config.stableConfigId))
                return new MeshStackBuildResult();

            var records = LoadStackRecords(config.avatarRoot);
            try
            {
                RemoveConfigLayers(records, config.stableConfigId, "Bulge");
                var result = RebuildBulgeStacks(config.avatarRoot, records, config);
                MeshReferenceTracker.StoreMesh(config, "original", null);
                MeshReferenceTracker.StoreMesh(config, "generated", null);
                if (config.additionalMeshes != null)
                {
                    foreach (var entry in config.additionalMeshes)
                    {
                        MeshReferenceTracker.StoreMesh(entry, "original", null);
                        MeshReferenceTracker.StoreMesh(entry, "generated", null);
                    }
                }
                return result;
            }
            catch
            {
                RestoreSnapshots(records);
                throw;
            }
        }

        public static bool HasLegacyBulgeGeneration(BulgeConfig config)
        {
            if (config == null || config.EffectiveDeformationMode != DeformationMode.Blendshape)
                return false;
            if (MeshReferenceTracker.ResolveMesh(config, "generated") != null)
                return true;
            if (config.additionalMeshes == null) return false;
            foreach (var entry in config.additionalMeshes)
            {
                if (MeshReferenceTracker.ResolveMesh(entry, "generated") != null)
                    return true;
            }
            return false;
        }

        public static MeshStackBuildResult RegisterLegacyBulgeGeneration(
            BulgeConfig config, string configAssetPath)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (config.avatarRoot == null)
                throw new InvalidOperationException("Avatar root is not set.");
            if (!HasLegacyBulgeGeneration(config))
                return new MeshStackBuildResult();

            config.EnsureStableConfigId();
            var records = LoadStackRecords(config.avatarRoot);
            try
            {
                if (ContainsLayer(records, config.stableConfigId, "Bulge"))
                    return new MeshStackBuildResult();

                int order = NextLayerOrder(records);
                bool addedLayer = false;
                foreach (var participant in GetBulgeParticipants(config))
                {
                    var generated = ResolveLegacyGenerated(config, participant);
                    if (generated == null) continue;

                    var renderer = BaseEffectConfig.ResolveRenderer(
                        config.avatarRoot, participant.rendererPath);
                    if (renderer == null) continue;

                    var record = GetOrCreateStackRecord(
                        records, config.avatarRoot, participant.rendererPath);
                    EnsureBaseMesh(record.stack, renderer,
                        ResolveLegacyOriginal(config, participant));

                    record.stack.layers.Add(CreateLegacyFrozenLayer(
                        config, configAssetPath, participant, order, generated));
                    addedLayer = true;
                }

                if (!addedLayer)
                    return new MeshStackBuildResult();

                var result = RebuildBulgeStacks(config.avatarRoot, records, config);
                UpdateLegacyReferences(config, records);
                return result;
            }
            catch
            {
                RestoreSnapshots(records);
                throw;
            }
        }

        public static MeshStackBuildResult RemoveBulgeLayerForRenderer(
            BulgeConfig config, string rendererPath)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (config.avatarRoot == null || string.IsNullOrEmpty(config.stableConfigId) ||
                string.IsNullOrEmpty(rendererPath))
                return new MeshStackBuildResult();

            var records = LoadStackRecords(config.avatarRoot);
            try
            {
                foreach (var record in records)
                {
                    if (record.stack.rendererPath != rendererPath)
                        continue;
                    record.stack.layers.RemoveAll(l =>
                        l.effectType == "Bulge" &&
                        l.stableConfigId == config.stableConfigId);
                }
                return RebuildBulgeStacks(config.avatarRoot, records, config);
            }
            catch
            {
                RestoreSnapshots(records);
                throw;
            }
        }

        public static MeshStackBuildResult RestoreAllBulgeLayers(BulgeConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (config.avatarRoot == null)
                return new MeshStackBuildResult();

            var records = LoadStackRecords(config.avatarRoot);
            try
            {
                foreach (var record in records)
                    record.stack.layers.RemoveAll(l => l.effectType == "Bulge");
                var result = RebuildBulgeStacks(config.avatarRoot, records, config);
                MeshReferenceTracker.StoreMesh(config, "original", null);
                MeshReferenceTracker.StoreMesh(config, "generated", null);
                if (config.additionalMeshes != null)
                {
                    foreach (var entry in config.additionalMeshes)
                    {
                        MeshReferenceTracker.StoreMesh(entry, "original", null);
                        MeshReferenceTracker.StoreMesh(entry, "generated", null);
                    }
                }
                return result;
            }
            catch
            {
                RestoreSnapshots(records);
                throw;
            }
        }

        public static bool HasBulgeLayer(BulgeConfig config)
        {
            if (config == null || config.avatarRoot == null ||
                string.IsNullOrEmpty(config.stableConfigId))
                return false;

            var records = LoadStackRecords(config.avatarRoot);
            foreach (var record in records)
            {
                foreach (var layer in record.stack.layers)
                {
                    if (layer.effectType == "Bulge" &&
                        layer.stableConfigId == config.stableConfigId)
                        return true;
                }
            }
            return false;
        }

        public static Mesh ResolveComposedMesh(
            GameObject avatarRoot, string rendererPath)
        {
            if (avatarRoot == null || string.IsNullOrEmpty(rendererPath))
                return null;

            string path = GetStackAssetPath(avatarRoot, rendererPath);
            var stack = AssetDatabase.LoadAssetAtPath<MeshStackAsset>(path);
            return stack != null ? stack.ResolveComposedMesh() : null;
        }

        public static string BuildScopedBulgeNamingPattern(BulgeConfig config)
        {
            if (config != null &&
                !string.IsNullOrEmpty(config.blendshapeNamingPattern) &&
                config.blendshapeNamingPattern.Contains("{0}"))
                return config.blendshapeNamingPattern;

            string id = ShortConfigId(config);
            return $"SPSBulge_{id}_Pos{{0}}";
        }

        private static MeshStackBuildResult RebuildBulgeStacks(
            GameObject avatarRoot, List<StackRecord> records, BulgeConfig currentConfig)
        {
            var oldAssigned = new Dictionary<string, Mesh>();
            var renderers = new Dictionary<string, SkinnedMeshRenderer>();
            var currentMeshes = new Dictionary<string, Mesh>();
            var result = new MeshStackBuildResult();
            string sessionId = DateTime.UtcNow.Ticks.ToString("x");

            try
            {
                foreach (var record in records)
                {
                    var stack = record.stack;
                    var renderer = BaseEffectConfig.ResolveRenderer(
                        avatarRoot, stack.rendererPath);
                    if (renderer != null)
                    {
                        renderers[stack.rendererPath] = renderer;
                        oldAssigned[stack.rendererPath] = renderer.sharedMesh;
                    }

                    var baseMesh = stack.ResolveBaseMesh();
                    if (baseMesh == null && renderer != null)
                    {
                        stack.StoreBaseMesh(renderer.sharedMesh);
                        baseMesh = renderer.sharedMesh;
                    }

                    currentMeshes[stack.rendererPath] = baseMesh;
                    if (renderer != null && baseMesh != null)
                    {
                        Undo.RecordObject(renderer, "Rebuild SPS Mesh Stack");
                        renderer.sharedMesh = baseMesh;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }
                }

                var groups = BuildLayerGroups(records);
                foreach (var group in groups)
                {
                    var layerConfig = ResolveBulgeConfigForGroup(
                        group, currentConfig, avatarRoot);
                    if (layerConfig == null)
                    {
                        Debug.LogWarning(
                            $"[SPS Mesh Stack] Missing Bulge config for layer " +
                            $"'{group.stableConfigId}'. Layer skipped.");
                        continue;
                    }

                    var primary = FindPrimaryLayer(group);
                    if (primary == null)
                    {
                        Debug.LogWarning(
                            $"[SPS Mesh Stack] Bulge layer '{group.stableConfigId}' " +
                            "has no primary renderer. Overlay layers were skipped.");
                        continue;
                    }

                    var orderedRefs = new List<LayerRef> { primary };
                    foreach (var layerRef in group.refs)
                    {
                        if (layerRef != primary &&
                            layerRef.layer.role == MeshStackLayerRole.Additional)
                            orderedRefs.Add(layerRef);
                    }

                    var generationRenderers = new List<SkinnedMeshRenderer>();
                    var generationRefs = new List<LayerRef>();
                    foreach (var layerRef in orderedRefs)
                    {
                        string rendererPath = layerRef.layer.rendererPath;
                        if (!renderers.TryGetValue(rendererPath, out var renderer))
                            continue;
                        if (!currentMeshes.TryGetValue(rendererPath, out var source) ||
                            source == null)
                            continue;

                        renderer.sharedMesh = source;
                        generationRenderers.Add(renderer);
                        generationRefs.Add(layerRef);
                    }

                    if (generationRenderers.Count == 0 ||
                        generationRefs[0].layer.role != MeshStackLayerRole.Primary)
                        continue;

                    if (IsLegacyFrozenGroup(group))
                    {
                        foreach (var layerRef in group.refs)
                        {
                            var mesh = layerRef.layer.ResolveOutputMesh();
                            if (mesh == null) continue;
                            currentMeshes[layerRef.layer.rendererPath] = mesh;
                            if (currentConfig != null &&
                                layerRef.layer.role == MeshStackLayerRole.Primary &&
                                layerRef.layer.stableConfigId == currentConfig.stableConfigId)
                            {
                                result.primaryBlendshapeNames =
                                    new List<string>(layerRef.layer.blendshapeNames);
                            }
                        }
                        continue;
                    }

                    string outputFolder = BuildLayerOutputFolder(
                        avatarRoot, layerConfig, group.order, sessionId);
                    string suffix = "_" + ShortConfigId(layerConfig) +
                        "_" + StableHash(group.order + ":" + group.stableConfigId);

                    var generated = BlendshapeGenerator.GenerateBulgeBlendshapes(
                        generationRenderers,
                        avatarRoot.transform,
                        layerConfig.pathWaypoints,
                        layerConfig.autoPositionCount,
                        layerConfig.blendshapeDisplacement,
                        outputFolder,
                        BuildScopedBulgeNamingPattern(layerConfig),
                        layerConfig.smoothingPasses,
                        layerConfig.subdivideAffectedRegion,
                        layerConfig.subdivisionPasses,
                        layerConfig.recalculateNormals,
                        layerConfig.normalFalloffSoftness,
                        layerConfig.normalSmoothingPasses,
                        layerConfig.normalBoundaryRings,
                        layerConfig.overlayMatchDistance,
                        suffix);

                    for (int i = 0; i < generated.Count && i < generationRefs.Count; i++)
                    {
                        var layerRef = generationRefs[i];
                        var mesh = generated[i].modifiedMesh;
                        currentMeshes[layerRef.layer.rendererPath] = mesh;
                        layerRef.layer.blendshapeNames =
                            new List<string>(generated[i].blendshapeNames);
                        layerRef.layer.StoreOutputMesh(mesh);

                        if (currentConfig != null &&
                            layerRef.layer.role == MeshStackLayerRole.Primary &&
                            layerRef.layer.stableConfigId == currentConfig.stableConfigId)
                        {
                            result.primaryBlendshapeNames =
                                new List<string>(generated[i].blendshapeNames);
                        }
                    }
                }

                foreach (var record in records)
                {
                    var stack = record.stack;
                    currentMeshes.TryGetValue(stack.rendererPath, out var finalMesh);
                    if (finalMesh == null)
                        finalMesh = stack.ResolveBaseMesh();

                    stack.StoreComposedMesh(finalMesh);
                    result.layerCount += stack.layers.Count;
                    if (finalMesh != null &&
                        renderers.TryGetValue(stack.rendererPath, out var renderer))
                    {
                        Undo.RecordObject(renderer, "Assign SPS Mesh Stack");
                        renderer.sharedMesh = finalMesh;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                        result.rendererCount++;
                    }
                }

                SaveStackRecords(records);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                return result;
            }
            catch
            {
                foreach (var kv in oldAssigned)
                {
                    if (renderers.TryGetValue(kv.Key, out var renderer))
                        renderer.sharedMesh = kv.Value;
                }
                throw;
            }
        }

        private static List<Participation> GetBulgeParticipants(BulgeConfig config)
        {
            var result = new List<Participation>();
            var seen = new HashSet<string>();
            if (!string.IsNullOrEmpty(config.rendererPath))
            {
                result.Add(new Participation
                {
                    rendererPath = config.rendererPath,
                    role = MeshStackLayerRole.Primary
                });
                seen.Add(config.rendererPath);
            }

            if (config.additionalMeshes == null) return result;
            foreach (var entry in config.additionalMeshes)
            {
                if (entry == null || string.IsNullOrEmpty(entry.rendererPath))
                    continue;
                if (seen.Contains(entry.rendererPath))
                    continue;
                result.Add(new Participation
                {
                    rendererPath = entry.rendererPath,
                    role = MeshStackLayerRole.Additional,
                    trackedMesh = entry
                });
                seen.Add(entry.rendererPath);
            }
            return result;
        }

        private static Mesh ResolveLegacyOriginal(
            BulgeConfig config, Participation participant)
        {
            if (participant.role == MeshStackLayerRole.Primary)
                return MeshReferenceTracker.ResolveMesh(config, "original");
            return MeshReferenceTracker.ResolveMesh(participant.trackedMesh, "original");
        }

        private static Mesh ResolveLegacyGenerated(
            BulgeConfig config, Participation participant)
        {
            if (participant.role == MeshStackLayerRole.Primary)
                return MeshReferenceTracker.ResolveMesh(config, "generated");
            return MeshReferenceTracker.ResolveMesh(participant.trackedMesh, "generated");
        }

        private static void RegisterAssignedLegacyBulgeGenerations(
            GameObject avatarRoot, List<StackRecord> records, string excludeStableConfigId)
        {
            var configs = ConfigAssetHelper.FindAllConfigs<BulgeConfig>();
            foreach (var legacyConfig in configs)
            {
                if (legacyConfig == null ||
                    legacyConfig.EffectiveDeformationMode != DeformationMode.Blendshape)
                    continue;
                if (!HasLegacyBulgeGeneration(legacyConfig))
                    continue;

                legacyConfig.EnsureStableConfigId();
                if (legacyConfig.stableConfigId == excludeStableConfigId ||
                    ContainsLayer(records, legacyConfig.stableConfigId, "Bulge"))
                    continue;

                bool addedAny = false;
                int order = NextLayerOrder(records);
                string configPath = AssetDatabase.GetAssetPath(legacyConfig);

                foreach (var participant in GetBulgeParticipants(legacyConfig))
                {
                    var generated = ResolveLegacyGenerated(legacyConfig, participant);
                    if (generated == null) continue;

                    var renderer = BaseEffectConfig.ResolveRenderer(
                        avatarRoot, participant.rendererPath);
                    if (renderer == null || renderer.sharedMesh != generated)
                        continue;

                    var record = GetOrCreateStackRecord(
                        records, avatarRoot, participant.rendererPath);
                    EnsureBaseMesh(record.stack, renderer,
                        ResolveLegacyOriginal(legacyConfig, participant));

                    var layer = CreateLegacyFrozenLayer(
                        legacyConfig, configPath, participant, order, generated);
                    record.stack.layers.Add(layer);
                    addedAny = true;
                }

                if (addedAny)
                    EditorUtility.SetDirty(legacyConfig);
            }
        }

        private static MeshStackLayer CreateLegacyFrozenLayer(
            BulgeConfig config,
            string configPath,
            Participation participant,
            int order,
            Mesh generated)
        {
            var layer = new MeshStackLayer
            {
                stableConfigId = config.stableConfigId,
                configPath = configPath,
                effectType = config.EffectTypeName,
                configurationName = config.configurationName,
                rendererPath = participant.rendererPath,
                role = participant.role,
                order = order,
                isEnabled = true,
                isLegacyFrozen = true,
                blendshapeNames = new List<string>(config.positionBlendshapes)
            };
            layer.StoreOutputMesh(generated);
            return layer;
        }

        private static void EnsureBaseMesh(
            MeshStackAsset stack, SkinnedMeshRenderer renderer, Mesh preferredBase)
        {
            if (stack.ResolveBaseMesh() != null) return;
            stack.StoreBaseMesh(preferredBase != null ? preferredBase : renderer.sharedMesh);
        }

        private static void UpdateLegacyReferences(
            BulgeConfig config, List<StackRecord> records)
        {
            foreach (var record in records)
            {
                var stack = record.stack;
                if (stack.rendererPath == config.rendererPath)
                {
                    MeshReferenceTracker.StoreMesh(config, "original", stack.ResolveBaseMesh());
                    MeshReferenceTracker.StoreMesh(config, "generated", stack.ResolveComposedMesh());
                    continue;
                }

                if (config.additionalMeshes == null) continue;
                foreach (var entry in config.additionalMeshes)
                {
                    if (entry == null || entry.rendererPath != stack.rendererPath)
                        continue;
                    MeshReferenceTracker.StoreMesh(entry, "original", stack.ResolveBaseMesh());
                    MeshReferenceTracker.StoreMesh(entry, "generated", stack.ResolveComposedMesh());
                }
            }
        }

        private static void ValidateBulgeNameCollisions(
            BulgeConfig config, List<StackRecord> records)
        {
            string pattern = BuildScopedBulgeNamingPattern(config);
            var candidateNames = new HashSet<string>();
            int count = Mathf.Max(0, config.autoPositionCount);
            for (int i = 1; i <= count; i++)
                candidateNames.Add(string.Format(pattern, i));

            foreach (var record in records)
            {
                bool hasCurrentLayer = false;
                foreach (var layer in record.stack.layers)
                {
                    if (layer.stableConfigId == config.stableConfigId)
                    {
                        hasCurrentLayer = true;
                        break;
                    }
                }
                if (!hasCurrentLayer) continue;

                var used = new HashSet<string>();
                AddMeshBlendshapeNames(record.stack.ResolveBaseMesh(), used);
                foreach (var layer in record.stack.layers)
                {
                    if (layer.stableConfigId == config.stableConfigId)
                        continue;
                    foreach (var name in layer.blendshapeNames)
                        used.Add(name);
                }

                foreach (var name in candidateNames)
                {
                    if (used.Contains(name))
                    {
                        throw new InvalidOperationException(
                            $"Blendshape name '{name}' already exists on renderer " +
                            $"'{record.stack.rendererPath}'. Change the naming pattern " +
                            "before generating this configuration.");
                    }
                }
            }
        }

        private static void AddMeshBlendshapeNames(Mesh mesh, HashSet<string> names)
        {
            if (mesh == null || names == null) return;
            for (int i = 0; i < mesh.blendShapeCount; i++)
                names.Add(mesh.GetBlendShapeName(i));
        }

        private static BulgeConfig ResolveBulgeConfigForGroup(
            LayerGroup group, BulgeConfig currentConfig, GameObject avatarRoot)
        {
            BulgeConfig config = null;
            if (currentConfig != null &&
                currentConfig.stableConfigId == group.stableConfigId)
            {
                config = currentConfig;
            }
            else if (!string.IsNullOrEmpty(group.configPath))
            {
                config = AssetDatabase.LoadAssetAtPath<BulgeConfig>(group.configPath);
            }

            if (config != null && config.avatarRoot == null)
                config.avatarRoot = avatarRoot;
            return config;
        }

        private static LayerRef FindPrimaryLayer(LayerGroup group)
        {
            foreach (var layerRef in group.refs)
            {
                if (layerRef.layer.role == MeshStackLayerRole.Primary)
                    return layerRef;
            }
            return null;
        }

        private static bool IsLegacyFrozenGroup(LayerGroup group)
        {
            if (group == null || group.refs.Count == 0) return false;
            foreach (var layerRef in group.refs)
            {
                if (!layerRef.layer.isLegacyFrozen)
                    return false;
            }
            return true;
        }

        private static List<LayerGroup> BuildLayerGroups(List<StackRecord> records)
        {
            var groups = new List<LayerGroup>();
            foreach (var record in records)
            {
                foreach (var layer in record.stack.layers)
                {
                    if (!layer.isEnabled || layer.effectType != "Bulge")
                        continue;

                    var group = FindGroup(groups, layer.stableConfigId);
                    if (group == null)
                    {
                        group = new LayerGroup
                        {
                            stableConfigId = layer.stableConfigId,
                            configPath = layer.configPath,
                            order = layer.order
                        };
                        groups.Add(group);
                    }
                    if (layer.order < group.order) group.order = layer.order;
                    if (string.IsNullOrEmpty(group.configPath))
                        group.configPath = layer.configPath;
                    group.refs.Add(new LayerRef { record = record, layer = layer });
                }
            }

            groups.Sort((a, b) =>
            {
                int c = a.order.CompareTo(b.order);
                return c != 0
                    ? c
                    : string.Compare(a.stableConfigId, b.stableConfigId,
                        StringComparison.Ordinal);
            });
            return groups;
        }

        private static LayerGroup FindGroup(List<LayerGroup> groups, string stableConfigId)
        {
            foreach (var group in groups)
            {
                if (group.stableConfigId == stableConfigId)
                    return group;
            }
            return null;
        }

        private static void RemoveConfigLayers(
            List<StackRecord> records, string stableConfigId, string effectType)
        {
            foreach (var record in records)
            {
                record.stack.layers.RemoveAll(l =>
                    l.stableConfigId == stableConfigId &&
                    (string.IsNullOrEmpty(effectType) || l.effectType == effectType));
            }
        }

        private static bool ContainsLayer(
            List<StackRecord> records, string stableConfigId, string effectType)
        {
            foreach (var record in records)
            {
                foreach (var layer in record.stack.layers)
                {
                    if (layer.stableConfigId == stableConfigId &&
                        (string.IsNullOrEmpty(effectType) || layer.effectType == effectType))
                        return true;
                }
            }
            return false;
        }

        private static int FindExistingOrder(
            List<StackRecord> records, string stableConfigId)
        {
            int order = int.MaxValue;
            foreach (var record in records)
            {
                foreach (var layer in record.stack.layers)
                {
                    if (layer.stableConfigId == stableConfigId && layer.order < order)
                        order = layer.order;
                }
            }
            return order == int.MaxValue ? -1 : order;
        }

        private static int NextLayerOrder(List<StackRecord> records)
        {
            int max = 0;
            foreach (var record in records)
            {
                foreach (var layer in record.stack.layers)
                    if (layer.order > max) max = layer.order;
            }
            return max + 1;
        }

        private static List<StackRecord> LoadStackRecords(GameObject avatarRoot)
        {
            var result = new List<StackRecord>();
            string folder = GetStackFolder(avatarRoot);
            if (!AssetDatabase.IsValidFolder(folder))
                return result;

            string[] guids = AssetDatabase.FindAssets("t:MeshStackAsset",
                new[] { folder });
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var stack = AssetDatabase.LoadAssetAtPath<MeshStackAsset>(path);
                if (stack == null) continue;
                result.Add(new StackRecord
                {
                    stack = stack,
                    assetPath = path,
                    snapshot = CaptureSnapshot(stack)
                });
            }
            return result;
        }

        private static StackRecord GetOrCreateStackRecord(
            List<StackRecord> records, GameObject avatarRoot, string rendererPath)
        {
            foreach (var record in records)
            {
                if (record.stack.rendererPath == rendererPath)
                    return record;
            }

            string path = GetStackAssetPath(avatarRoot, rendererPath);
            var stack = ScriptableObject.CreateInstance<MeshStackAsset>();
            stack.avatarRootName = avatarRoot.name;
            stack.rendererPath = rendererPath;
            stack.name = System.IO.Path.GetFileNameWithoutExtension(path);

            var created = new StackRecord
            {
                stack = stack,
                assetPath = path,
                isNew = true
            };
            records.Add(created);
            return created;
        }

        private static void SaveStackRecords(List<StackRecord> records)
        {
            foreach (var record in records)
            {
                if (record.isNew)
                {
                    string folder = System.IO.Path.GetDirectoryName(record.assetPath)
                        ?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(folder))
                        SpsAnimationUtility.EnsureFolder(folder);
                    AssetDatabase.CreateAsset(record.stack, record.assetPath);
                    record.isNew = false;
                }
                EditorUtility.SetDirty(record.stack);
            }
        }

        private static StackSnapshot CaptureSnapshot(MeshStackAsset stack)
        {
            return new StackSnapshot
            {
                baseMesh = stack.baseMesh,
                baseMeshPath = stack.baseMeshPath,
                baseMeshGuid = stack.baseMeshGuid,
                composedMesh = stack.composedMesh,
                composedMeshPath = stack.composedMeshPath,
                composedMeshGuid = stack.composedMeshGuid,
                layers = CloneLayers(stack.layers)
            };
        }

        private static void RestoreSnapshots(List<StackRecord> records)
        {
            foreach (var record in records)
            {
                if (record.isNew || record.snapshot == null)
                    continue;
                var snapshot = record.snapshot;
                record.stack.baseMesh = snapshot.baseMesh;
                record.stack.baseMeshPath = snapshot.baseMeshPath;
                record.stack.baseMeshGuid = snapshot.baseMeshGuid;
                record.stack.composedMesh = snapshot.composedMesh;
                record.stack.composedMeshPath = snapshot.composedMeshPath;
                record.stack.composedMeshGuid = snapshot.composedMeshGuid;
                record.stack.layers = CloneLayers(snapshot.layers);
            }
        }

        private static List<MeshStackLayer> CloneLayers(List<MeshStackLayer> layers)
        {
            var copy = new List<MeshStackLayer>();
            if (layers == null) return copy;
            foreach (var layer in layers)
            {
                copy.Add(new MeshStackLayer
                {
                    stableConfigId = layer.stableConfigId,
                    configPath = layer.configPath,
                    effectType = layer.effectType,
                    configurationName = layer.configurationName,
                    rendererPath = layer.rendererPath,
                    role = layer.role,
                    order = layer.order,
                    isEnabled = layer.isEnabled,
                    isLegacyFrozen = layer.isLegacyFrozen,
                    blendshapeNames = new List<string>(layer.blendshapeNames),
                    outputMesh = layer.outputMesh,
                    outputMeshPath = layer.outputMeshPath,
                    outputMeshGuid = layer.outputMeshGuid
                });
            }
            return copy;
        }

        private static string BuildLayerOutputFolder(
            GameObject avatarRoot, BulgeConfig config, int order, string sessionId)
        {
            string baseFolder = config.GetOutputFolder();
            if (string.IsNullOrEmpty(baseFolder))
            {
                string avatar = BaseEffectConfig.SanitizeFileName(avatarRoot.name);
                baseFolder = $"Assets/SPSTools/{avatar}/Bulge/{ShortConfigId(config)}";
            }
            return $"{baseFolder}/{StackFolderName}/{GeneratedFolderName}/" +
                $"{order:000}_{ShortConfigId(config)}_{sessionId}";
        }

        private static string GetStackFolder(GameObject avatarRoot)
        {
            string avatar = BaseEffectConfig.SanitizeFileName(avatarRoot.name);
            return $"Assets/SPSTools/{avatar}/{StackFolderName}";
        }

        private static string GetStackAssetPath(GameObject avatarRoot, string rendererPath)
        {
            string folder = GetStackFolder(avatarRoot);
            string hash = StableHash(rendererPath);
            string leaf = rendererPath;
            int slash = leaf.LastIndexOf('/');
            if (slash >= 0) leaf = leaf.Substring(slash + 1);
            leaf = BaseEffectConfig.SanitizeFileName(leaf);
            if (string.IsNullOrEmpty(leaf)) leaf = "Renderer";
            return $"{folder}/{hash}_{leaf}_Stack.asset";
        }

        private static string ShortConfigId(BulgeConfig config)
        {
            string id = config != null ? config.stableConfigId : "";
            if (string.IsNullOrEmpty(id))
                id = "unsaved";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        private static string StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                if (value != null)
                {
                    for (int i = 0; i < value.Length; i++)
                    {
                        hash ^= value[i];
                        hash *= 16777619;
                    }
                }
                return hash.ToString("x8");
            }
        }
    }
}
