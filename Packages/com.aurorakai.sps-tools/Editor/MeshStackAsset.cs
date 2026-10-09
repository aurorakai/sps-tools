using System;
using System.Collections.Generic;
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

        public void StoreOutputMesh(Mesh mesh) =>
            MeshReferenceTracker.Store(mesh, out outputMesh, out outputMeshPath, out outputMeshGuid);

        public Mesh ResolveOutputMesh()
        {
            outputMesh = MeshReferenceTracker.LoadStored(
                outputMesh, outputMeshPath, outputMeshGuid, out _);
            return outputMesh;
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

        public void StoreBaseMesh(Mesh mesh) =>
            MeshReferenceTracker.Store(mesh, out baseMesh, out baseMeshPath, out baseMeshGuid);

        public void StoreComposedMesh(Mesh mesh) =>
            MeshReferenceTracker.Store(mesh, out composedMesh, out composedMeshPath, out composedMeshGuid);

        public Mesh ResolveBaseMesh()
        {
            baseMesh = MeshReferenceTracker.LoadStored(baseMesh, baseMeshPath, baseMeshGuid, out _);
            return baseMesh;
        }

        public Mesh ResolveComposedMesh()
        {
            composedMesh = MeshReferenceTracker.LoadStored(
                composedMesh, composedMeshPath, composedMeshGuid, out _);
            return composedMesh;
        }
    }
}
