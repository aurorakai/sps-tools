using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    public enum MeshStackLayerRole
    {
        Primary,
        Additional
    }

    [Serializable]
    public class MeshStackLayer
    {
        public string stableConfigId = "";
        public string configPath = "";
        public string effectType = "";
        public string configurationName = "";
        public string rendererPath = "";
        public MeshStackLayerRole role;
        public int order;
        public bool isEnabled = true;
        public bool isLegacyFrozen;
        public List<string> blendshapeNames = new List<string>();

        public Mesh outputMesh;
        public string outputMeshPath = "";
        public string outputMeshGuid = "";

        public void StoreOutputMesh(Mesh mesh)
        {
            outputMesh = mesh;
            outputMeshPath = mesh != null ? AssetDatabase.GetAssetPath(mesh) : "";
            outputMeshGuid = !string.IsNullOrEmpty(outputMeshPath)
                ? AssetDatabase.AssetPathToGUID(outputMeshPath)
                : "";
        }

        public Mesh ResolveOutputMesh()
        {
            outputMesh = ResolveStoredMesh(outputMesh, outputMeshPath, outputMeshGuid);
            return outputMesh;
        }

        private static Mesh ResolveStoredMesh(Mesh mesh, string path, string guid)
        {
            if (mesh != null) return mesh;
            string resolvedPath = path;
            if (!string.IsNullOrEmpty(guid))
            {
                string guidPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(guidPath))
                    resolvedPath = guidPath;
                else if (!string.IsNullOrEmpty(path) && AssetDatabase.AssetPathToGUID(path) != guid)
                    return null;
            }
            if (string.IsNullOrEmpty(resolvedPath)) return null;
            return AssetDatabase.LoadAssetAtPath<Mesh>(resolvedPath);
        }
    }

    public class MeshStackAsset : ScriptableObject
    {
        public string avatarRootName = "";
        public string rendererPath = "";

        public Mesh baseMesh;
        public string baseMeshPath = "";
        public string baseMeshGuid = "";

        public Mesh composedMesh;
        public string composedMeshPath = "";
        public string composedMeshGuid = "";

        public List<MeshStackLayer> layers = new List<MeshStackLayer>();

        public void StoreBaseMesh(Mesh mesh)
        {
            baseMesh = mesh;
            baseMeshPath = mesh != null ? AssetDatabase.GetAssetPath(mesh) : "";
            baseMeshGuid = !string.IsNullOrEmpty(baseMeshPath)
                ? AssetDatabase.AssetPathToGUID(baseMeshPath)
                : "";
        }

        public void StoreComposedMesh(Mesh mesh)
        {
            composedMesh = mesh;
            composedMeshPath = mesh != null ? AssetDatabase.GetAssetPath(mesh) : "";
            composedMeshGuid = !string.IsNullOrEmpty(composedMeshPath)
                ? AssetDatabase.AssetPathToGUID(composedMeshPath)
                : "";
        }

        public Mesh ResolveBaseMesh()
        {
            baseMesh = ResolveStoredMesh(baseMesh, baseMeshPath, baseMeshGuid);
            return baseMesh;
        }

        public Mesh ResolveComposedMesh()
        {
            composedMesh = ResolveStoredMesh(composedMesh, composedMeshPath, composedMeshGuid);
            return composedMesh;
        }

        private static Mesh ResolveStoredMesh(Mesh mesh, string path, string guid)
        {
            if (mesh != null) return mesh;
            string resolvedPath = path;
            if (!string.IsNullOrEmpty(guid))
            {
                string guidPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(guidPath))
                    resolvedPath = guidPath;
                else if (!string.IsNullOrEmpty(path) && AssetDatabase.AssetPathToGUID(path) != guid)
                    return null;
            }
            if (string.IsNullOrEmpty(resolvedPath)) return null;
            return AssetDatabase.LoadAssetAtPath<Mesh>(resolvedPath);
        }
    }
}
