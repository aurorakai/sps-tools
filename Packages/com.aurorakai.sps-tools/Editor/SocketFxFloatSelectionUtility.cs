using System.Collections.Generic;

namespace AuroraKai.SPSTools
{
    public static class SocketFxFloatSelectionUtility
    {
        public static string GetSelectedParameter(
            BaseEffectConfig config, DetectedSocket socket)
        {
            if (socket == null || socket.depthFxFloats == null ||
                socket.depthFxFloats.Count == 0)
            {
                return "";
            }

            string key = GetSocketKey(socket);
            if (config?.socketFxFloatSelections != null)
            {
                foreach (var selection in config.socketFxFloatSelections)
                {
                    if (selection == null || selection.socketPath != key) continue;
                    if (socket.depthFxFloats.Contains(selection.parameterName))
                        return selection.parameterName;
                    break;
                }
            }

            return socket.depthFxFloats[0];
        }

        public static void SetSelectedParameter(
            BaseEffectConfig config, DetectedSocket socket, string parameterName)
        {
            if (config == null || socket == null ||
                string.IsNullOrEmpty(parameterName))
            {
                return;
            }

            if (config.socketFxFloatSelections == null)
                config.socketFxFloatSelections = new List<SocketFxFloatSelection>();

            string key = GetSocketKey(socket);
            foreach (var selection in config.socketFxFloatSelections)
            {
                if (selection == null || selection.socketPath != key) continue;
                selection.parameterName = parameterName;
                return;
            }

            config.socketFxFloatSelections.Add(new SocketFxFloatSelection
            {
                socketPath = key,
                parameterName = parameterName
            });
        }

        public static List<string> GetEnabledDepthParameters(
            BaseEffectConfig config, List<DetectedSocket> detectedSockets)
        {
            var result = new List<string>();
            if (config?.enabledSocketIndices == null || detectedSockets == null)
                return result;

            foreach (int idx in config.enabledSocketIndices)
            {
                if (idx < 0 || idx >= detectedSockets.Count) continue;

                string parameter = GetSelectedParameter(config, detectedSockets[idx]);
                if (!string.IsNullOrEmpty(parameter) && !result.Contains(parameter))
                    result.Add(parameter);
            }

            return result;
        }

        public static string MakeUniqueParameterName(
            string baseName, DetectedSocket socket)
        {
            if (string.IsNullOrEmpty(baseName))
                baseName = "SPS_Depth";

            if (socket?.depthFxFloats == null ||
                !socket.depthFxFloats.Contains(baseName))
            {
                return baseName;
            }

            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{baseName}_{i}";
                if (!socket.depthFxFloats.Contains(candidate))
                    return candidate;
            }

            return $"{baseName}_{System.Guid.NewGuid():N}";
        }

        private static string GetSocketKey(DetectedSocket socket)
        {
            if (!string.IsNullOrEmpty(socket.gameObjectPath))
                return socket.gameObjectPath;
            if (!string.IsNullOrEmpty(socket.gameObjectName))
                return socket.gameObjectName;
            return socket.DisplayName;
        }
    }
}
