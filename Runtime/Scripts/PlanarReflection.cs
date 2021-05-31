using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[ExecuteAlways]
public class PlanarReflection : MonoBehaviour
{
    private int planarReflectionTextureID = Shader.PropertyToID("_PlanarReflectionTexture");
    private Camera reflectionCamera = null;
    private RenderTexture reflectionRenderTexture = null;

    public enum RenderResolution
    {
        DOUBLE = 200,
        SINGLE = 100,
        HALF = 50,
        QUARTER = 25,
        EIGHT = 12
    }
    
    [Serializable]
    public class ReflectionSettings
    {
        public RenderResolution renderScale = RenderResolution.HALF;

        public float clipPlaneOffset;
        public LayerMask cullingMask;
        public bool renderShadows;
    }

    [SerializeField]
    private ReflectionSettings settings;

    private void OnEnable()
    {
        RenderPipelineManager.beginCameraRendering += RenderReflections;
    }

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= RenderReflections;

        if (reflectionCamera)
        {
            reflectionCamera.targetTexture = null;

            if (Application.isEditor)
                DestroyImmediate(reflectionCamera.gameObject);
            else
                Destroy(reflectionCamera.gameObject);
        }

        if (reflectionRenderTexture)
        {
            RenderTexture.ReleaseTemporary(reflectionRenderTexture);
            reflectionRenderTexture = null;
        }
    }

    private void RenderReflections(ScriptableRenderContext srpContext, Camera camera)
    {
        var max = QualitySettings.maximumLODLevel;
        var bias = QualitySettings.lodBias;
        var fog = RenderSettings.fog;
        
        //Change render settings for reflection cam.
        GL.invertCulling = true;
        RenderSettings.fog = false;
        QualitySettings.maximumLODLevel = 1;
        QualitySettings.lodBias = bias * 0.5f;

        //Update position
        UpdateReflectionCamera(camera);

        //Create render texture and calculate resolution.
        if (reflectionRenderTexture == null)
        {
            var resolution = GetRenderResolution(camera, UniversalRenderPipeline.asset.renderScale);
            reflectionRenderTexture = RenderTexture.GetTemporary((int) resolution.x, (int) resolution.y, 16,
                GraphicsFormatUtility.GetGraphicsFormat(RenderTextureFormat.DefaultHDR, true));
            reflectionRenderTexture.useMipMap = true;
            reflectionRenderTexture.autoGenerateMips = true;
        }

        reflectionCamera.targetTexture = reflectionRenderTexture;
        UniversalRenderPipeline.RenderSingleCamera(srpContext, reflectionCamera);

        //Revert settings back.
        GL.invertCulling = false;
        RenderSettings.fog = fog;
        QualitySettings.maximumLODLevel = max;
        QualitySettings.lodBias = bias;

        //Set reflection texture.
        Shader.SetGlobalTexture(planarReflectionTextureID, reflectionRenderTexture);
    }

    private Vector2 GetRenderResolution(Camera camera, float urpRenderScale) =>
        camera.pixelRect.size * urpRenderScale * (Mathf.Max((float)RenderResolution.EIGHT,(float)settings.renderScale) / 100f);

    private void UpdateReflectionCamera(Camera camera)
    {
        if (reflectionCamera == null)
            CreateReflectionCamera(camera);

        var planeNormal = transform.up;
        var planePosition = transform.position + planeNormal * settings.clipPlaneOffset;

        float direction = -Vector3.Dot(planeNormal, planePosition) - settings.clipPlaneOffset;
        Vector4 reflectionPlane = new Vector4(planeNormal.x, planeNormal.y, planeNormal.z, direction);
        
        //Copy camera settings
        reflectionCamera.CopyFrom(camera);
        reflectionCamera.useOcclusionCulling = false;

        //Create new position from a reflection on the y axis.
        Vector3 oldPosition = camera.transform.position - planePosition;
        Vector3 newPosition = Vector3.Reflect(oldPosition, planeNormal);
        reflectionCamera.transform.forward = Vector3.Reflect(camera.transform.forward, planeNormal);

        //Calculate reflection matrix
        Matrix4x4 reflectionMatrix = Matrix4x4.identity * Matrix4x4.Scale(new Vector3(1,-1,1));
        CalculateReflectionMatrix(ref reflectionMatrix, reflectionPlane);
        
        reflectionCamera.worldToCameraMatrix = camera.worldToCameraMatrix * reflectionMatrix;

        //Calculate reflection camera's near clip plane.
        Vector4 reflectionClipPlane =
            CalculateReflectionClipPlane(reflectionCamera, planePosition - planeNormal * 0.1f, planeNormal, 1f);
        reflectionCamera.projectionMatrix = camera.CalculateObliqueMatrix(reflectionClipPlane);
        reflectionCamera.cullingMask = settings.cullingMask;
        reflectionCamera.transform.position = newPosition;
    }

    /// <summary>
    /// Based on the Householder reflection matrix.
    /// </summary>
    /// <param name="matrix"></param>
    /// <param name="plane"></param>
    private void CalculateReflectionMatrix(ref Matrix4x4 matrix, Vector4 plane)
    {
        matrix.m00 = (1F - 2F * plane[0] * plane[0]);
        matrix.m01 = (-2F * plane[0] * plane[1]);
        matrix.m02 = (-2F * plane[0] * plane[2]);
        matrix.m03 = (-2F * plane[3] * plane[0]);

        matrix.m10 = (-2F * plane[1] * plane[0]);
        matrix.m11 = (1F - 2F * plane[1] * plane[1]);
        matrix.m12 = (-2F * plane[1] * plane[2]);
        matrix.m13 = (-2F * plane[3] * plane[1]);

        matrix.m20 = (-2F * plane[2] * plane[0]);
        matrix.m21 = (-2F * plane[2] * plane[1]);
        matrix.m22 = (1F - 2F * plane[2] * plane[2]);
        matrix.m23 = (-2F * plane[3] * plane[2]);

        matrix.m30 = 0F;
        matrix.m31 = 0F;
        matrix.m32 = 0F;
        matrix.m33 = 1F;
    }

    private Vector4 CalculateReflectionClipPlane(Camera cam, Vector3 position, Vector3 normal, float sign)
    {
        Vector3 offsetPosition = position + normal * settings.clipPlaneOffset;
        Matrix4x4 m = cam.worldToCameraMatrix;
        Vector3 clipPosition = m.MultiplyPoint(offsetPosition);
        Vector3 clipNormal = m.MultiplyVector(normal).normalized * sign;

        return new Vector4(clipNormal.x, clipNormal.y, clipNormal.z, -Vector3.Dot(clipPosition, clipNormal));
    }

    private void CreateReflectionCamera(Camera camera)
    {
        GameObject reflectionCamGO = new GameObject($"{camera.name}'s Reflection Camera", typeof(Camera));

        var additionalCameraData = reflectionCamGO.AddComponent<UniversalAdditionalCameraData>();
        additionalCameraData.renderShadows = settings.renderShadows;
        additionalCameraData.requiresColorOption = CameraOverrideOption.Off;
        additionalCameraData.requiresDepthOption = CameraOverrideOption.Off;

        reflectionCamera = reflectionCamGO.GetComponent<Camera>();
        reflectionCamera.CopyFrom(camera);
        reflectionCamera.enabled = false;

        reflectionCamGO.hideFlags = HideFlags.HideAndDontSave;
    }
}