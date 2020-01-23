﻿using System;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using Unity.ClusterRendering.Toolkit;
using UnityEngine;
using UnityEngine.Rendering;

[ExecuteInEditMode]
public class CustomDepthBuffer : MonoBehaviour
{
    [SerializeField]
    string m_Id;

    public string id { get { return m_Id; } }

    [SerializeField]
    Transform[] m_OccludingRoots;
    
    [SerializeField]
    Mesh[] m_OccludingMeshes;

    [SerializeField]
    Vector2Int m_RenderTargetSize;
    
    [SerializeField]
    Material m_Material;

    [SerializeField]
    bool m_ShowDebugUI;
    
    RenderTexture m_ColorBuffer;
    RenderTexture m_DepthBuffer;
    CommandBuffer m_CmdBuffer;

    public RenderTexture target { get { return m_DepthBuffer; } }

    struct InstancedDrawArgs
    {
        public Mesh mesh;
        public List<Matrix4x4> transforms; // TODO use arrays if we use CommandBuffer
    }
    
    List<InstancedDrawArgs> m_RenderingData = new List<InstancedDrawArgs>();

    static List<CustomDepthBuffer> s_Instances = new List<CustomDepthBuffer>();

    public static RenderTexture GetTarget(string name)
    {
        // we expect very few instances, optimize if it were to change
        foreach (var instance in s_Instances) 
        {
            if (instance.id == name)
                return instance.target;
        }
        return null;
    }

    class InstancingDataGenerationVisitor
    {
        Dictionary<int, Mesh> m_Meshes = new Dictionary<int, Mesh>();
        Dictionary<int, List<Matrix4x4>> m_Transforms = new Dictionary<int, List<Matrix4x4>>();
        HashSet<int> m_Filter = new HashSet<int>();
        
        public void Reset()
        {
            m_Meshes.Clear();
            m_Transforms.Clear();
        }

        public void SetAllowedMeshes(Mesh[] meshes)
        {
            m_Filter.Clear();
            foreach (var mesh in meshes)
                m_Filter.Add(mesh.GetInstanceID());
        }

        public void Visit(Transform trs)
        {
            var filter = trs.GetComponent<MeshFilter>();
            if (filter == null)
                return;
            
            var mesh = filter.sharedMesh;
            if (mesh == null)
                return;

            TryAddInstance(mesh, trs.localToWorldMatrix);
        }

        public void PopulateRenderingData(List<InstancedDrawArgs> renderingData)
        {
            renderingData.Clear();

            foreach (var entry in m_Meshes)
            {
                renderingData.Add(new InstancedDrawArgs
                {
                    mesh = entry.Value,
                    transforms = m_Transforms[entry.Key]
                });
            }
        }

        void TryAddInstance(Mesh mesh, Matrix4x4 transform)
        {
            var meshId = mesh.GetInstanceID();

            if (!m_Filter.Contains(meshId))
                return;

            if (m_Meshes.ContainsKey(meshId))
            {
                m_Transforms[meshId].Add(transform);
            }
            else 
            {
                m_Meshes.Add(meshId, mesh);
                var list = new List<Matrix4x4>();
                list.Add(transform);
                m_Transforms.Add(meshId, list);
            }
        }
    }
    
    InstancingDataGenerationVisitor m_InstancingDataGenerationVisitor = new InstancingDataGenerationVisitor();

    void OnEnable()
    {
        var shader = Shader.Find("HDRP/Lit");
        //m_Material = new Material(shader);
        //m_Material.enableInstancing = true;
        m_CmdBuffer = new CommandBuffer();

        s_Instances.Add(this);
        
        UpdateRenderingData();
    }

    void OnDisable()
    {
        s_Instances.Remove(this);
        
        //DestroyImmediate(m_Material);
        m_CmdBuffer.Release();
        if (m_DepthBuffer != null)
            m_DepthBuffer.Release();
        m_DepthBuffer = null;
        if (m_ColorBuffer != null)
            m_ColorBuffer.Release();
        m_ColorBuffer = null;
    }

    void OnGUI()
    {
        if (m_ShowDebugUI && m_DepthBuffer != null) 
            GUI.DrawTexture(new Rect(0, Screen.height, Screen.width, -Screen.height), m_DepthBuffer);        
    }

    // TODO optim only render if camera moved
    void UpdateCommandBuffer(Camera camera)
    {
        if (m_DepthBuffer == null || m_DepthBuffer.width != m_RenderTargetSize.x || m_DepthBuffer.height != m_RenderTargetSize.y)
        {
            if (m_DepthBuffer != null)
                m_DepthBuffer.Release();
            if (m_ColorBuffer != null)
                m_ColorBuffer.Release();
            
            m_DepthBuffer = new RenderTexture(m_RenderTargetSize.x, m_RenderTargetSize.y, 0, RenderTextureFormat.Depth);
            m_ColorBuffer = new RenderTexture(m_RenderTargetSize.x, m_RenderTargetSize.y, 0, RenderTextureFormat.Default);
        }

        m_CmdBuffer.Clear();
        m_CmdBuffer.SetRenderTarget(m_ColorBuffer.colorBuffer, m_DepthBuffer.colorBuffer);
        m_CmdBuffer.ClearRenderTarget(true, true, Color.black);

        var projectionMatrix = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
        var viewMatrix = camera.worldToCameraMatrix;
        var viewProjectionMatrix = camera.projectionMatrix * camera.worldToCameraMatrix;
        
        // as we use HDRP we cannot rely on CommandBuffer.SetViewProjectionMatrices(...);
        // we need to manually update uniforms
        m_CmdBuffer.SetGlobalMatrix("_ViewMatrix", viewMatrix);
        m_CmdBuffer.SetGlobalMatrix("_ProjMatrix", projectionMatrix);
        m_CmdBuffer.SetGlobalMatrix("_InvProjMatrix", projectionMatrix.inverse);
        m_CmdBuffer.SetGlobalMatrix("_ViewProjMatrix", viewProjectionMatrix);
        m_CmdBuffer.SetGlobalMatrix("_InvViewProjMatrix", viewProjectionMatrix.inverse);
        m_CmdBuffer.SetGlobalMatrix("_CameraViewProjMatrix", viewProjectionMatrix);
        m_CmdBuffer.SetGlobalVector("_WorldSpaceCameraPos", Vector3.zero);
        m_CmdBuffer.SetGlobalVector("_ShadowClipPlanes", Vector3.zero);
        
        // TODO fix saturated depth
        var f = camera.farClipPlane;
        var n = camera.nearClipPlane;
        var zBufferParams = new Vector4(1 - f / n, f / n, 1 / f - 1 / n, 1 / n);
        //var zBufferParams = new Vector4(-1 + f / n, 1, -1 / f + 1 / n, 1 / f);
        m_CmdBuffer.SetGlobalVector("_ZBufferParams", zBufferParams);
        
        foreach (var args in m_RenderingData) 
        {
            for (var i = 0; i != args.mesh.subMeshCount; ++i)
                m_CmdBuffer.DrawMeshInstanced(args.mesh, i, m_Material, 0, args.transforms.ToArray());
        }
    }

    void Update()
    {
        var camera = Camera.main;
        if (camera != null && camera.cameraType == CameraType.Game)
        {
            UpdateCommandBuffer(camera);
            Graphics.ExecuteCommandBuffer(m_CmdBuffer);
        }
    }

    [ContextMenu("Update Rendering Data")]
    void UpdateRenderingData()
    {
        if (m_OccludingRoots == null)
            return;
        
        m_InstancingDataGenerationVisitor.Reset();
        m_InstancingDataGenerationVisitor.SetAllowedMeshes(m_OccludingMeshes);

        foreach (var occluder in m_OccludingRoots)
        {
            UpdateRenderingDataRecursive(occluder.transform, m_InstancingDataGenerationVisitor);
        }
        
        m_InstancingDataGenerationVisitor.PopulateRenderingData(m_RenderingData);
        
        // TMP DEBUG
        Debug.Log("INSTANCING RENDERING DATA:");
        foreach (var data in m_RenderingData)
        {
            Debug.Log($"Mesh name[{data.mesh.name}] id[{data.mesh.GetInstanceID()}] count[{data.transforms.Count}]");
        }
    }

    static void UpdateRenderingDataRecursive(Transform parent, InstancingDataGenerationVisitor visitor) 
    {
        foreach (Transform child in parent)
        {
            visitor.Visit(child);
            UpdateRenderingDataRecursive(child, visitor);
        }
    }
}
