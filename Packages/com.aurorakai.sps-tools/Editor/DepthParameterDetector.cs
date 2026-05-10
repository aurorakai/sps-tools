using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Represents a detected SPS Socket on an avatar.
    /// </summary>
    public class DetectedSocket
    {
        public string socketName;           // Human-readable socket name
        public string gameObjectName;       // The GameObject the socket is on
        public string gameObjectPath;       // Full path from avatar root
        public Component component;         // The VRCFury socket component

        /// <summary>
        /// FX Float parameter names found in the socket's Depth Animations.
        /// These are "Set an FX Float" entries that map SPS depth to a named parameter.
        /// </summary>
        public List<string> depthFxFloats = new List<string>();

        /// <summary>
        /// Convenience: first detected FX Float parameter, or empty string.
        /// </summary>
        public string depthParameter =>
            depthFxFloats.Count > 0 ? depthFxFloats[0] : "";

        public string DisplayName =>
            !string.IsNullOrEmpty(socketName) ? socketName : gameObjectName;

        public override string ToString() =>
            depthFxFloats.Count > 0
                ? $"{DisplayName} → {depthParameter}"
                : $"{DisplayName} (no FX Float set)";
    }

    /// <summary>
    /// Scans VRCFury components on an avatar for SPS Sockets and their depth parameters.
    ///
    /// VRCFury SPS Sockets are VRCFuryHapticSocket components (VF.Component namespace).
    /// Depth actions are in field "depthActions2" (List of DepthActionNew).
    /// Each DepthActionNew has an "actionSet" (State) with an "actions" list ([SerializeReference]).
    /// FxFloatAction entries in that list map SPS depth to named FX parameters.
    /// </summary>
    public static class DepthParameterDetector
    {
        // Cached VRCFury type references (resolved once per domain reload)
        private static Type s_hapticSocketType;
        private static Type s_fxFloatActionType;
        private static Type s_depthActionNewType;
        private static Type s_stateType;
        private static bool s_typesResolved;

        // Resolved "Plugs" enum value, set on new depth actions so the FX Float
        // sweeps 0..1 across the plug's insertion depth regardless of plug scale.
        private static object s_unitsPlugsValue;

        /// <summary>
        /// Finds all SPS Sockets on the avatar by scanning for VRCFuryHapticSocket components.
        /// </summary>
        public static List<DetectedSocket> FindSPSSockets(GameObject avatarRoot)
        {
            var sockets = new List<DetectedSocket>();
            if (avatarRoot == null) return sockets;

            ResolveTypes();

            var allComponents = avatarRoot.GetComponentsInChildren<Component>(true);
            foreach (var comp in allComponents)
            {
                if (comp == null) continue;

                var compType = comp.GetType();

                // Path 1: VRCFuryHapticSocket (the standard socket component)
                if (s_hapticSocketType != null && s_hapticSocketType.IsAssignableFrom(compType))
                {
                    var socket = ExtractFromHapticSocket(comp, avatarRoot);
                    if (socket != null) sockets.Add(socket);
                    continue;
                }

                // Path 2: Component type name contains "HapticSocket" or "Socket"
                // (fallback for when type resolution fails or VRCFury version differs)
                string typeName = compType.Name;
                if (typeName.Contains("HapticSocket") || typeName.Contains("SPSSocket"))
                {
                    var socket = ExtractFromHapticSocket(comp, avatarRoot);
                    if (socket != null) sockets.Add(socket);
                }
            }

            return sockets;
        }

        /// <summary>
        /// Checks if the avatar root has a VRC_AvatarDescriptor component.
        /// </summary>
        public static bool HasAvatarDescriptor(GameObject avatarRoot)
            => FindAvatarDescriptor(avatarRoot) != null;

        private static Component FindAvatarDescriptor(GameObject go)
        {
            if (go == null) return null;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                if (comp.GetType().FullName?.Contains("AvatarDescriptor") == true)
                    return comp;
            }
            return null;
        }

        /// <summary>
        /// Gets the FX layer AnimatorController from the VRC Avatar Descriptor.
        /// VRC Avatar Descriptor has baseAnimationLayers[4] (FX layer index)
        /// with an animatorController field.
        /// Returns null if not found.
        /// </summary>
        public static RuntimeAnimatorController GetFXLayerController(GameObject avatarRoot)
        {
            if (avatarRoot == null) return null;

            var descriptor = FindAvatarDescriptor(avatarRoot);
            if (descriptor == null) return null;

            // baseAnimationLayers is an array of CustomAnimLayer structs
            object layers = GetFieldValueRecursive(descriptor, "baseAnimationLayers");
            if (layers == null) return null;

            if (layers is System.Array layerArray)
            {
                // Identify the FX layer by enum NAME rather than value — VRCSDK3
                // reorders the AnimLayerType enum across versions (FX has been 4, 5,
                // and 6 in different releases) and VRCFury-modified descriptors may
                // reorder the baseAnimationLayers array itself, so neither
                // `typeInt == N` nor `i == N` is a reliable index. Matching on the
                // enum's string name ("FX") is stable across all versions.
                for (int i = 0; i < layerArray.Length; i++)
                {
                    object layer = layerArray.GetValue(i);
                    if (layer == null) continue;

                    object typeVal = GetFieldValueRecursive(layer, "type");
                    if (typeVal == null) continue;

                    // Enum.ToString() yields the symbolic name, e.g. "FX", "Action".
                    if (!string.Equals(typeVal.ToString(), "FX",
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    object controller = GetFieldValueRecursive(layer, "animatorController");
                    if (controller is RuntimeAnimatorController rac)
                        return rac;
                }
            }

            return null;
        }

        /// <summary>
        /// Finds a single avatar root in the scene by searching for a
        /// VRC_AvatarDescriptor component. Returns null if zero or more
        /// than one descriptor is found.
        /// </summary>
        public static GameObject FindSingleAvatarRoot()
        {
            GameObject found = null;

            foreach (var go in UnityEngine.SceneManagement.SceneManager
                         .GetActiveScene().GetRootGameObjects())
            {
                SearchForDescriptor(go.transform, ref found, out bool multiple);
                if (multiple) return null;
            }

            return found;
        }

        private static void SearchForDescriptor(
            Transform current, ref GameObject found, out bool multiple)
        {
            multiple = false;

            if (FindAvatarDescriptor(current.gameObject) != null)
            {
                if (found != null)
                {
                    multiple = true;
                    return;
                }
                found = current.gameObject;
            }

            for (int i = 0; i < current.childCount; i++)
            {
                SearchForDescriptor(current.GetChild(i), ref found, out multiple);
                if (multiple) return;
            }
        }

        /// <summary>
        /// Validates that a parameter name is non-empty and usable.
        /// </summary>
        public static bool IsValidParameterName(string parameterName)
        {
            return !string.IsNullOrWhiteSpace(parameterName);
        }

        /// <summary>
        /// Generates a suggested FX Float parameter name from a socket.
        /// e.g. socket named "Anal" → "SPS_Depth_Anal"
        /// </summary>
        public static string SuggestParameterName(DetectedSocket socket)
        {
            string name = socket.socketName;
            if (string.IsNullOrEmpty(name))
                name = socket.gameObjectName;

            var sanitized = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    sanitized.Append(c);
            }

            string suffix = sanitized.Length > 0 ? $"_{sanitized}" : "";
            return $"SPS_Depth{suffix}";
        }

        /// <summary>
        /// Adds a "Set an FX Float" depth animation entry to a VRCFury SPS Socket.
        /// Creates a DepthActionNew with an FxFloatAction mapping full depth range to the parameter.
        ///
        /// VRCFury structure:
        ///   VRCFuryHapticSocket.depthActions2 (List of DepthActionNew)
        ///     → DepthActionNew.actionSet (State)
        ///       → State.actions (List of Action, [SerializeReference])
        ///         → FxFloatAction { name, value }
        /// </summary>
        public static bool AddFxFloatToSocket(Component socketComponent, string parameterName)
        {
            if (socketComponent == null || string.IsNullOrEmpty(parameterName))
                return false;

            ResolveTypes();

            try
            {
                // RegisterCompleteObjectUndo does a deep clone snapshot of the component,
                // which correctly captures [SerializeReference] list deltas — RecordObject's
                // shallow snapshot can leave VRCFury's depthActions2 in malformed state on
                // Ctrl+Z. Reflection-based mutation below is unchanged; only the undo
                // strategy is upgraded.
                Undo.RegisterCompleteObjectUndo(socketComponent, "Add SPS Depth FX Float");

                // Get the depthActions2 list
                object actionsList = GetFieldValueRecursive(socketComponent, "depthActions2");

                if (!(actionsList is IList list))
                {
                    Debug.LogError("[SPS Effects] Could not find depthActions2 field. " +
                        "Use Debug Properties to inspect the socket structure.");
                    return false;
                }

                // Create DepthActionNew instance
                Type depthActionType = s_depthActionNewType;
                if (depthActionType == null)
                {
                    // Infer from list element type
                    depthActionType = GetListElementType(actionsList.GetType());
                }
                if (depthActionType == null)
                {
                    Debug.LogError("[SPS Effects] Could not determine DepthActionNew type.");
                    return false;
                }

                object depthAction = Activator.CreateInstance(depthActionType);

                // VRCFury's defaults are range=(-0.25, 0) in Meters — scale-dependent,
                // so a short plug saturates the FX param before reaching 1.0. Prefer
                // Plugs units with range (-1, 0): the parameter sweeps 0→1 across the
                // plug's full insertion depth regardless of plug size. Fall back to
                // VRCFury's defaults on older versions where Plugs isn't defined.
                bool configuredPlugs = false;
                if (s_unitsPlugsValue != null &&
                    SetFieldIfExists(depthAction, "units", s_unitsPlugsValue))
                {
                    SetFieldIfExists(depthAction, "range", new Vector2(-1f, 0f));
                    configuredPlugs = true;
                }
                if (!configuredPlugs)
                {
                    Debug.LogWarning("[SPS Effects] DepthActionUnits.Plugs not available; " +
                        "socket depth action will use VRCFury defaults. You may need to adjust " +
                        "the activation distance manually in the socket inspector.");
                }
                SetFieldIfExists(depthAction, "enableSelf", true);
                SetFieldIfExists(depthAction, "smoothingSeconds", 0f);

                // Get or create the actionSet (State)
                object state = GetFieldValueRecursive(depthAction, "actionSet");
                if (state == null && s_stateType != null)
                {
                    state = Activator.CreateInstance(s_stateType);
                    SetFieldIfExists(depthAction, "actionSet", state);
                }
                if (state == null)
                {
                    Debug.LogError("[SPS Effects] Could not access or create State for depth action.");
                    return false;
                }

                // Get or create the actions list inside State
                object stateActions = GetFieldValueRecursive(state, "actions");
                if (!(stateActions is IList stateActionsList))
                {
                    Debug.LogError("[SPS Effects] Could not access State.actions list.");
                    return false;
                }

                // Create FxFloatAction
                if (s_fxFloatActionType == null)
                {
                    Debug.LogError("[SPS Effects] Could not find FxFloatAction type.");
                    return false;
                }

                object fxFloat = Activator.CreateInstance(s_fxFloatActionType);
                SetFieldIfExists(fxFloat, "name", parameterName);
                SetFieldIfExists(fxFloat, "value", 1f);

                // Wire it all up
                stateActionsList.Add(fxFloat);
                list.Add(depthAction);

                EditorUtility.SetDirty(socketComponent);

                if (!Application.isPlaying)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                        socketComponent.gameObject.scene);

                string rangeDesc = configuredPlugs
                    ? "range=(-1, 0) Plugs"
                    : "VRCFury default range (Meters)";
                Debug.Log($"[SPS Effects] Added depth FX Float '{parameterName}' to " +
                    $"SPS Socket on '{socketComponent.gameObject.name}' ({rangeDesc}).");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SPS Effects] Failed to add FX Float to socket: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Dumps all serialized properties of a component to the console.
        /// </summary>
        public static void DumpComponentProperties(Component comp)
        {
            if (comp == null) return;
            var so = new SerializedObject(comp);
            var prop = so.GetIterator();

            Debug.Log($"[SPS Debug] === Component: {comp.GetType().FullName} " +
                $"(Assembly: {comp.GetType().Assembly.GetName().Name}) ===");

            if (prop.NextVisible(true))
            {
                do
                {
                    string refType = prop.propertyType == SerializedPropertyType.ManagedReference
                        ? $" [{prop.managedReferenceFullTypename}]"
                        : "";
                    string value = prop.propertyType switch
                    {
                        SerializedPropertyType.String => $" = \"{prop.stringValue}\"",
                        SerializedPropertyType.Float => $" = {prop.floatValue}",
                        SerializedPropertyType.Integer => $" = {prop.intValue}",
                        SerializedPropertyType.Boolean => $" = {prop.boolValue}",
                        SerializedPropertyType.Enum => $" = {prop.enumValueIndex}",
                        SerializedPropertyType.Vector2 => $" = {prop.vector2Value}",
                        _ => ""
                    };
                    Debug.Log($"[SPS Debug] {prop.propertyPath} ({prop.propertyType}){refType}{value}");
                }
                while (prop.NextVisible(true));
            }
            so.Dispose();
        }

        // =====================================================================
        // Type resolution
        // =====================================================================

        private static void ResolveTypes()
        {
            // Only latch the "resolved" flag after we've actually found the
            // primary VRCFury type. The old flow set it true unconditionally,
            // which meant a call that happened before VRCFury's assembly
            // finished loading (Unity startup, mid-compile) would permanently
            // latch null type references and force every subsequent call to
            // use name-match fallbacks until the next domain reload.
            if (s_typesResolved) return;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = assembly.GetName().Name;
                // VRCFury runtime assembly is typically named "VRCFury" or contains "vrcfury"
                if (!asmName.Equals("VRCFury", StringComparison.OrdinalIgnoreCase) &&
                    !asmName.Contains("vrcfury", StringComparison.OrdinalIgnoreCase))
                    continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        $"[SPS Effects] Could not inspect VRCFury assembly '{asmName}' " +
                        $"for socket/action types: {e.Message}");
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null) continue;
                    switch (type.Name)
                    {
                        case "VRCFuryHapticSocket":
                            s_hapticSocketType = type;
                            foreach (var nested in type.GetNestedTypes(
                                BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                if (nested.Name == "DepthActionNew")
                                    s_depthActionNewType = nested;
                            }
                            break;
                        case "FxFloatAction":
                            s_fxFloatActionType = type;
                            break;
                        case "State":
                            if (type.Namespace?.Contains("VF") == true)
                                s_stateType = type;
                            break;
                    }
                }
            }

            // DepthActionNew might not be nested - search by name too
            if (s_depthActionNewType == null)
                s_depthActionNewType = FindTypeInVRCFury("DepthActionNew");

            // DepthActionUnits is a file-level enum in VRCFury (Meters, Plugs, Local).
            // We cache the "Plugs" value so new depth actions use scale-invariant units.
            var depthActionUnitsType = FindTypeInVRCFury("DepthActionUnits");
            if (depthActionUnitsType != null && depthActionUnitsType.IsEnum)
            {
                try { s_unitsPlugsValue = Enum.Parse(depthActionUnitsType, "Plugs"); }
                catch (ArgumentException e)
                {
                    Debug.LogWarning(
                        "[SPS Effects] VRCFury DepthActionUnits has no 'Plugs' value; " +
                        $"socket depth actions will use VRCFury defaults. {e.Message}");
                }
            }

            // Only latch once every type AddFxFloatToSocket needs is resolved.
            // If any are missing (VRCFury still loading, or a future version
            // renamed/relocated a type), keep retrying so we don't permanently
            // disable depth-action wiring until domain reload.
            bool allCriticalResolved =
                s_hapticSocketType != null &&
                s_fxFloatActionType != null &&
                s_stateType != null &&
                s_depthActionNewType != null;

            if (s_hapticSocketType == null)
            {
                Debug.LogWarning("[SPS Effects] Could not resolve VRCFuryHapticSocket type. " +
                    "Socket detection will use fallback name matching.");
            }
            else if (!allCriticalResolved)
            {
                Debug.LogWarning("[SPS Effects] VRCFury types partially resolved. " +
                    $"FxFloatAction={s_fxFloatActionType != null}, " +
                    $"State={s_stateType != null}, DepthActionNew={s_depthActionNewType != null}. " +
                    "Will retry on next call.");
            }

            s_typesResolved = allCriticalResolved;
        }

        private static Type FindTypeInVRCFury(string simpleName)
        {
            return ReflectionUtil.FindType(asm =>
                {
                    string asmName = asm.GetName().Name;
                    return asmName.Equals("VRCFury", StringComparison.OrdinalIgnoreCase)
                        || asmName.Contains("vrcfury", StringComparison.OrdinalIgnoreCase);
                },
                t => t.Name == simpleName);
        }

        // =====================================================================
        // Socket extraction - VRCFuryHapticSocket (current format)
        // =====================================================================

        private static DetectedSocket ExtractFromHapticSocket(Component comp, GameObject avatarRoot)
        {
            var socket = new DetectedSocket
            {
                gameObjectName = comp.gameObject.name,
                gameObjectPath = BaseEffectConfig.GetRelativePath(avatarRoot.transform, comp.transform),
                component = comp
            };

            // Read socket name directly from the "name" field
            object nameVal = GetFieldValueRecursive(comp, "name");
            socket.socketName = nameVal as string ?? "";

            // Scan depthActions2 for FxFloatAction entries
            socket.depthFxFloats = ExtractFxFloatsFromDepthActions(comp);

            return socket;
        }

        /// <summary>
        /// Reads depthActions2 (List of DepthActionNew) and extracts FxFloatAction parameter names.
        /// Each DepthActionNew has: actionSet (State) → actions (List of Action [SerializeReference])
        /// </summary>
        private static List<string> ExtractFxFloatsFromDepthActions(Component comp)
        {
            var results = new List<string>();

            // Get depthActions2 list
            object depthActions = GetFieldValueRecursive(comp, "depthActions2");
            if (!(depthActions is IList depthList)) return results;

            foreach (object depthAction in depthList)
            {
                if (depthAction == null) continue;

                object state = GetFieldValueRecursive(depthAction, "actionSet");
                if (state == null) continue;

                // Get actions list from State
                object actions = GetFieldValueRecursive(state, "actions");
                if (!(actions is IList actionList)) continue;

                foreach (object action in actionList)
                {
                    if (action == null) continue;

                    // Check if this is an FxFloatAction
                    string actionTypeName = action.GetType().Name;
                    if (actionTypeName == "FxFloatAction" ||
                        actionTypeName.Contains("FxFloat") ||
                        actionTypeName.Contains("FloatAction"))
                    {
                        object paramName = GetFieldValueRecursive(action, "name");
                        if (paramName is string name && !string.IsNullOrEmpty(name) &&
                            !results.Contains(name))
                        {
                            results.Add(name);
                        }
                    }
                }
            }

            return results;
        }

        // =====================================================================
        // Reflection helpers
        // =====================================================================

        private static object GetFieldValueRecursive(object obj, string fieldName)
        {
            if (obj == null) return null;
            var type = obj.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            while (type != null)
            {
                var field = type.GetField(fieldName, flags);
                if (field != null) return field.GetValue(obj);
                type = type.BaseType;
            }
            return null;
        }

        private static bool SetFieldIfExists(object obj, string fieldName, object value)
        {
            if (obj == null) return false;
            var type = obj.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            while (type != null)
            {
                var field = type.GetField(fieldName, flags);
                if (field != null)
                {
                    field.SetValue(obj, value);
                    return true;
                }
                type = type.BaseType;
            }
            return false;
        }

        private static Type GetListElementType(Type listType)
        {
            if (listType.IsGenericType)
                return listType.GetGenericArguments()[0];
            if (listType.IsArray)
                return listType.GetElementType();
            return null;
        }

    }
}
