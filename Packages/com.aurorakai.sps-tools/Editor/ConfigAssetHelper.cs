using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    public static class ConfigAssetHelper
    {
        public static List<T> FindAllConfigs<T>() where T : BaseEffectConfig
        {
            var results = new List<T>();

            // Search the whole Assets folder - configs may live anywhere the user saved them
            string[] guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var config = AssetDatabase.LoadAssetAtPath<T>(path);
                if (config != null)
                    results.Add(config);
            }
            return results;
        }
    }
}
