using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Creates VRCFury FullController components via reflection.
    /// Zero hard dependency on VRCFury assemblies.
    /// </summary>
    public static class VRCFuryIntegration
    {
        /// <summary>
        /// Returns true if VRCFury is installed and the FullController type is resolvable.
        /// </summary>
        public static bool IsVRCFuryInstalled()
        {
            return FindType("VRCFury") != null;
        }

        /// <summary>
        /// Returns the detected VRCFury version string, or "unknown".
        /// </summary>
        public static string GetVRCFuryVersion()
        {
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!assembly.FullName.Contains("VRCFury") &&
                        !assembly.FullName.Contains("vrcfury"))
                        continue;

                    // Try assembly version
                    var version = assembly.GetName().Version;
                    if (version != null && (version.Major > 0 || version.Minor > 0))
                        return version.ToString();

                    // Try informational version attribute
                    var attrs = assembly.GetCustomAttributes(
                        typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                    if (attrs.Length > 0)
                    {
                        var attr = (System.Reflection.AssemblyInformationalVersionAttribute)attrs[0];
                        if (!string.IsNullOrEmpty(attr.InformationalVersion))
                            return attr.InformationalVersion;
                    }
                }
                catch { }
            }
            return "unknown";
        }

        /// <summary>
        /// Creates a child GameObject under avatarRoot with a VRCFury FullController component.
        /// If a child with the same name exists, it is replaced only after the
        /// new controller is fully configured.
        /// Returns the created GameObject, or null if VRCFury is not installed.
        /// </summary>
        public static GameObject CreateFullController(
            GameObject avatarRoot,
            string gameObjectName,
            RuntimeAnimatorController controller,
            string depthParameter)
        {
            return CreateFullController(
                avatarRoot, gameObjectName, controller, new[] { depthParameter });
        }

        /// <summary>
        /// Creates a child GameObject under avatarRoot with a VRCFury FullController
        /// component and registers every provided depth parameter as a global param.
        /// </summary>
        public static GameObject CreateFullController(
            GameObject avatarRoot,
            string gameObjectName,
            RuntimeAnimatorController controller,
            IEnumerable<string> depthParameters)
        {
            if (avatarRoot == null)
            {
                Debug.LogError("[SPS Effects] Avatar root is null. Cannot create FullController.");
                return null;
            }
            if (controller == null)
            {
                Debug.LogError("[SPS Effects] Controller asset is null. Cannot create FullController.");
                return null;
            }

            var vrcFuryType = FindType("VRCFury");
            if (vrcFuryType == null)
            {
                Debug.LogError("[SPS Effects] VRCFury is not installed. Cannot create FullController.");
                return null;
            }

            Transform existing = avatarRoot.transform.Find(gameObjectName);
            string tempName = $"{gameObjectName} (Temp)";

            // Group the entire stage-configure-swap sequence into a single undo
            // step so the user can Ctrl+Z the whole operation atomically - and
            // a mid-flight recompile or scene save that interrupts us leaves a
            // recoverable state rather than two half-configured objects.
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName($"Configure {gameObjectName}");

            var stagedGo = new GameObject(tempName);
            stagedGo.transform.SetParent(avatarRoot.transform, false);
            Undo.RegisterCreatedObjectUndo(stagedGo, $"Create {gameObjectName}");

            Component comp = null;
            try
            {
                comp = Undo.AddComponent(stagedGo, vrcFuryType);

                var fullController = CreateFeatureInstance("FullController");
                if (fullController == null)
                {
                    Debug.LogError("[SPS Effects] Failed to create VRCFury FullController feature.");
                    CleanupFailedStage(stagedGo);
                    Undo.CollapseUndoOperations(undoGroup);
                    return null;
                }

                if (!AssignContent(comp, fullController))
                    throw new InvalidOperationException("VRCFury content property was not found.");

                var so = new SerializedObject(comp);
                try
                {
                    ConfigureController(so, controller);

                    var rootBindings = so.FindProperty("content.rootBindingsApplyToAvatar");
                    if (rootBindings != null)
                        rootBindings.boolValue = true;

                    ConfigureGlobalParams(so, depthParameters);
                    so.ApplyModifiedProperties();
                }
                finally
                {
                    so.Dispose();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SPS Effects] Failed to configure VRCFury component: {e.Message}. " +
                    "This may be due to a VRCFury version incompatibility.");
                CleanupFailedStage(stagedGo);
                Undo.CollapseUndoOperations(undoGroup);
                return null;
            }

            // Atomic swap: remove the old object first (it's fully configured
            // state-wise and we've committed the staged one), then rename the
            // temp. Unity tolerates the brief duplicate-name state during the
            // single frame between these two calls.
            if (existing != null)
                Undo.DestroyObjectImmediate(existing.gameObject);

            stagedGo.name = gameObjectName;

            EditorUtility.SetDirty(comp);
            EditorUtility.SetDirty(stagedGo);

            if (!Application.isPlaying)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(stagedGo.scene);

            Undo.CollapseUndoOperations(undoGroup);
            return stagedGo;
        }

        // --- Reflection helpers ---

        private static void ConfigureController(SerializedObject so, RuntimeAnimatorController controller)
        {
            var controllersProp = so.FindProperty("content.controllers");
            if (controllersProp == null || !controllersProp.isArray)
                throw new InvalidOperationException("VRCFury controller list was not found.");

            controllersProp.ClearArray();
            controllersProp.InsertArrayElementAtIndex(0);
            var entry = controllersProp.GetArrayElementAtIndex(0);

            // Set controller reference (VRCFury uses GuidWrapper with objRef field)
            var controllerRef = entry.FindPropertyRelative("controller");
            if (controllerRef == null)
                throw new InvalidOperationException("VRCFury controller reference was not found.");

            var objRef = controllerRef.FindPropertyRelative("objRef");
            if (objRef == null)
                throw new InvalidOperationException("VRCFury controller objRef was not found.");

            objRef.objectReferenceValue = controller;
            var idProp = controllerRef.FindPropertyRelative("id");
            if (idProp != null)
                idProp.stringValue = "";

            // Set type to FX (enum index 5)
            var typeProp = entry.FindPropertyRelative("type");
            if (typeProp == null)
                throw new InvalidOperationException("VRCFury controller type was not found.");
            typeProp.enumValueIndex = 5;
        }

        private static void ConfigureGlobalParams(
            SerializedObject so, IEnumerable<string> depthParameters)
        {
            var globalParams = so.FindProperty("content.globalParams");
            if (globalParams == null || !globalParams.isArray)
                throw new InvalidOperationException("VRCFury globalParams list was not found.");

            globalParams.ClearArray();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (depthParameters == null) return;

            foreach (var depthParameter in depthParameters)
            {
                if (string.IsNullOrWhiteSpace(depthParameter) || !seen.Add(depthParameter))
                    continue;

                int idx = globalParams.arraySize;
                globalParams.InsertArrayElementAtIndex(idx);
                globalParams.GetArrayElementAtIndex(idx).stringValue = depthParameter;
            }
        }

        private static void CleanupFailedStage(GameObject stagedGo)
        {
            if (stagedGo != null)
                Undo.DestroyObjectImmediate(stagedGo);
        }

        internal static Type FindType(string simpleName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.Name == simpleName &&
                            (type.Namespace?.Contains("VRCFury") == true ||
                             type.Namespace?.Contains("vrcfury") == true ||
                             assembly.FullName.Contains("VRCFury")))
                            return type;
                    }
                }
                catch { /* ReflectionTypeLoadException - skip */ }
            }
            return null;
        }

        private static Type FindFeatureType(string simpleName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!assembly.FullName.Contains("VRCFury") && !assembly.FullName.Contains("vrcfury"))
                        continue;
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsAbstract || type.IsInterface) continue;
                        if (type.Name == simpleName && type.Namespace != null &&
                            type.Namespace.Contains("Feature"))
                            return type;
                    }
                }
                catch { /* ReflectionTypeLoadException - skip */ }
            }
            return null;
        }

        private static object CreateFeatureInstance(string featureTypeName)
        {
            var type = FindFeatureType(featureTypeName);
            if (type == null) return null;

            var instance = Activator.CreateInstance(type);

            // Set version to latest to prevent upgrade migrations
            FieldInfo versionField = null;
            var current = type;
            while (current != null && versionField == null)
            {
                versionField = current.GetField("version",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                current = current.BaseType;
            }

            if (versionField != null)
            {
                var getLatestVersion = type.GetMethod("GetLatestVersion",
                    BindingFlags.Instance | BindingFlags.Public);
                if (getLatestVersion != null)
                {
                    int latest = (int)getLatestVersion.Invoke(instance, null);
                    versionField.SetValue(instance, latest);
                }
            }

            return instance;
        }

        private static bool AssignContent(Component comp, object feature)
        {
            var so = new SerializedObject(comp);
            var contentProp = so.FindProperty("content");
            if (contentProp == null)
            {
                so.Dispose();
                return false;
            }

            contentProp.managedReferenceValue = feature;
            so.ApplyModifiedProperties();
            so.Dispose();
            return true;
        }
    }
}
