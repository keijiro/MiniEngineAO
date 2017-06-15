using UnityEngine;
using UnityEngine.Rendering;

namespace ComputeAO
{
    [RequireComponent(typeof(Camera))]
    public class AmbientOcclusion : MonoBehaviour
    {
        #region Built-in resources

        [SerializeField, HideInInspector] Shader _shader;
        [SerializeField, HideInInspector] ComputeShader _compute;

        #endregion

        #region Temporary objects

        RenderTexture _aoBuffer;
        Material _blitMaterial;
        CommandBuffer _aoCommand;
        CommandBuffer _blitCommand;

        #endregion

        #region Private functions

        static void DestroyIfAlive(Object o)
        {
            if (o != null)
                if (Application.isPlaying)
                    Destroy(o);
                else
                    DestroyImmediate(o);
        }

        void SetAOParameters()
        {
            _compute.
        }

        #endregion

        #region MonoBehaviour functions

        void Start()
        {
            var camera = GetComponent<Camera>();

            camera.depthTextureMode |= DepthTextureMode.Depth;

            // Allocate a render texture with UAV for storing AO.
            _aoBuffer = new RenderTexture(
                camera.pixelWidth, camera.pixelHeight, 0,
                RenderTextureFormat.R8, RenderTextureReadWrite.Linear
            );
            _aoBuffer.enableRandomWrite = true;
            _aoBuffer.Create();

            // Set up a command buffer for the AO estimator.
            _aoCommand = new CommandBuffer();
            _aoCommand.name = "AO Estimator";

            var kernel = _compute.FindKernel("AmbientOcclusion");

            _aoCommand.SetComputeTextureParam(
                _compute, kernel, "DepthTexture", BuiltinRenderTextureType.ResolvedDepth
            );

            _aoCommand.SetComputeTextureParam(
                _compute, kernel, "AOTexture", _aoBuffer
            );

            SetAOParameters();

            uint sizeX, sizeY, sizeZ;
            _compute.GetKernelThreadGroupSizes(kernel, out sizeX, out sizeY, out sizeZ);

            _aoCommand.DispatchCompute(
                _compute, kernel,
                camera.pixelWidth / (int)sizeX,
                camera.pixelHeight / (int)sizeY,
                1
            );

            // Execute it before lighting.
            camera.AddCommandBuffer(CameraEvent.BeforeLighting, _aoCommand);

            // Set up a command buffer for final composition.
            _blitCommand = new CommandBuffer();
            _blitCommand.name = "AO Composition";

            _blitCommand.SetGlobalTexture("_AOTexture", _aoBuffer);

            _blitMaterial = new Material(_shader);
            _blitCommand.Blit(
                null, BuiltinRenderTextureType.CurrentActive, _blitMaterial, 0
            );

            // Execute it before image effects.
            camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _blitCommand);
        }

        void OnEnable()
        {
            if (_aoCommand != null)
            {
                var camera = GetComponent<Camera>();
                camera.AddCommandBuffer(CameraEvent.BeforeLighting, _aoCommand);
                camera.AddCommandBuffer(CameraEvent.BeforeLighting, _blitCommand);
            }
        }

        void OnDisable()
        {
            if (_aoCommand != null)
            {
                var camera = GetComponent<Camera>();
                camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, _aoCommand);
                camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, _blitCommand);
            }
        }

        void OnDestroy()
        {
            DestroyIfAlive(_blitMaterial);
            DestroyIfAlive(_aoBuffer);
            _aoCommand.Dispose();
            _blitCommand.Dispose();
        }

        #endregion
    }
}
