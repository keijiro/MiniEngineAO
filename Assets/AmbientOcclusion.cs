using UnityEngine;
using UnityEngine.Rendering;

namespace ComputeAO
{
    [RequireComponent(typeof(Camera))]
    public sealed class AmbientOcclusion : MonoBehaviour
    {
        #region Exposed attributes

        [SerializeField, Range(1, 10)] float _rejectionFalloff = 2.5f;
        [SerializeField, Range(0, 1)] float _accentuation = 0.1f;

        #endregion

        #region Built-in resources

        [SerializeField, HideInInspector] ComputeShader _setup1Compute;
        [SerializeField, HideInInspector] ComputeShader _setup2Compute;
        [SerializeField, HideInInspector] ComputeShader _aoCompute;
        [SerializeField, HideInInspector] Shader _debugShader;

        #endregion

        #region Temporary objects

        RenderTexture _aoBuffer;
        RenderTexture _linearDepthBuffer;
        RenderTexture _downsizedDepthBuffer1;
        RenderTexture _downsizedDepthBuffer2;
        RenderTexture _downsizedDepthBuffer3;
        RenderTexture _downsizedDepthBuffer4;
        RenderTexture _tiledDepthBuffer1;
        RenderTexture _tiledDepthBuffer2;
        RenderTexture _tiledDepthBuffer3;
        RenderTexture _tiledDepthBuffer4;
        CommandBuffer _aoCommand;

        Material _debugMaterial;
        CommandBuffer _debugCommand;

        #endregion

        #region Private functions

        static void DestroyIfAlive(Object o)
        {
            if (o != null)
            {
                if (Application.isPlaying)
                    Destroy(o);
                else
                    DestroyImmediate(o);
            }
        }

        #endregion

        #region MonoBehaviour functions

        void Start()
        {
            var camera = GetComponent<Camera>();

            // Requires the camera depth texture.
            camera.depthTextureMode |= DepthTextureMode.Depth;

            // Initialize the AO buffer.
            _aoBuffer = CreateUAVBuffer(camera.pixelWidth, camera.pixelHeight, 1, 8);

            // Set up the AO command buffer.
            // It's to be invoked before deferred lighting.
            _aoCommand = new CommandBuffer();
            _aoCommand.name = "SSAO";
            AddDepthSetUpCommands(_aoCommand, camera);
            AddAOCommands(_aoCommand, camera);
            camera.AddCommandBuffer(CameraEvent.BeforeLighting, _aoCommand);

            // Set up the debug command buffer.
            // It's to be invoked before image effects.
            _debugMaterial = new Material(_debugShader);
            _debugCommand = new CommandBuffer();
            _debugCommand.name = "SSAO Debug";
            AddDebugCommands(_debugCommand);
            camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _debugCommand);
        }

        void OnEnable()
        {
            if (_aoCommand != null)
            {
                var camera = GetComponent<Camera>();
                camera.AddCommandBuffer(CameraEvent.BeforeLighting, _aoCommand);
                camera.AddCommandBuffer(CameraEvent.BeforeLighting, _debugCommand);
            }
        }

        void OnDisable()
        {
            if (_aoCommand != null)
            {
                var camera = GetComponent<Camera>();
                camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, _aoCommand);
                camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, _debugCommand);
            }
        }

        void OnDestroy()
        {
            DestroyIfAlive(_aoBuffer);
            DestroyIfAlive(_debugMaterial);
            _aoCommand.Dispose();
            _debugCommand.Dispose();
        }

        #endregion

        #region Command buffer builders

        internal static class TextureID
        {
            static public readonly int linearDepth = Shader.PropertyToID("LinearZ");
            static public readonly int downsizedDepth1 = Shader.PropertyToID("DS2x");
            static public readonly int downsizedDepth2 = Shader.PropertyToID("DS4x");
            static public readonly int tiledDepth1 = Shader.PropertyToID("DS2xAtlas");
            static public readonly int tiledDepth2 = Shader.PropertyToID("DS4xAtlas");
        }

        RenderTexture CreateUAVBuffer(int width, int height, int depth, int bpp)
        {
            var format = RenderTextureFormat.R8;

            if (bpp == 16) format = RenderTextureFormat.RHalf;
            if (bpp == 32) format = RenderTextureFormat.RFloat;

            var rt = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);

            rt.enableRandomWrite = true;

            if (depth > 1)
            {
                rt.dimension = TextureDimension.Tex2DArray;
                rt.volumeDepth = depth;
            }

            rt.Create();
            return rt;
        }

        // Calculate values in _ZBuferParams (built-in shader variable)
        // We can't use _ZBufferParams in compute shaders, so this function is
        // used to give the values in it to compute shaders.
        Vector4 CalculateZBufferParams(Camera camera)
        {
            var fpn = camera.farClipPlane / camera.nearClipPlane;
            if (SystemInfo.usesReversedZBuffer)
                return new Vector4(fpn - 1, 1, 0, 0);
            else
                return new Vector4(1 - fpn, fpn, 0, 0);
        }

        void AddDepthSetUpCommands(CommandBuffer cmd, Camera camera)
        {
            var w = camera.pixelWidth;
            var h = camera.pixelHeight;

            // Allocate temporary depth buffers.
            _linearDepthBuffer = CreateUAVBuffer(w, h, 1, 16);
            _downsizedDepthBuffer1 = CreateUAVBuffer((w +  1) /  2, (h +  1) /  2, 1, 32);
            _downsizedDepthBuffer2 = CreateUAVBuffer((w +  3) /  4, (h +  3) /  4, 1, 32);
            _downsizedDepthBuffer3 = CreateUAVBuffer((w +  7) /  8, (h +  7) /  8, 1, 32);
            _downsizedDepthBuffer4 = CreateUAVBuffer((w + 15) / 16, (h + 15) / 16, 1, 32);
            _tiledDepthBuffer1 = CreateUAVBuffer((w +  7) /  8, (h +  7) /  8, 16, 32);
            _tiledDepthBuffer2 = CreateUAVBuffer((w + 15) / 16, (h + 15) / 16, 16, 32);
            _tiledDepthBuffer3 = CreateUAVBuffer((w + 31) / 32, (h + 31) / 32, 16, 32);
            _tiledDepthBuffer4 = CreateUAVBuffer((w + 63) / 64, (h + 63) / 64, 16, 32);

            // 1st downsampling pass.
            var kernel = _setup1Compute.FindKernel("main");
            _aoCommand.SetComputeTextureParam(_setup1Compute, kernel, "LinearZ", _linearDepthBuffer);
            _aoCommand.SetComputeTextureParam(_setup1Compute, kernel, "DS2x", _downsizedDepthBuffer1);
            _aoCommand.SetComputeTextureParam(_setup1Compute, kernel, "DS4x", _downsizedDepthBuffer2);
            _aoCommand.SetComputeTextureParam(_setup1Compute, kernel, "DS2xAtlas", _tiledDepthBuffer1);
            _aoCommand.SetComputeTextureParam(_setup1Compute, kernel, "DS4xAtlas", _tiledDepthBuffer2);
            _aoCommand.SetComputeTextureParam(_setup1Compute, kernel, "Depth", BuiltinRenderTextureType.ResolvedDepth);
            _aoCommand.SetComputeVectorParam(_setup1Compute, "ZBufferParams", CalculateZBufferParams(camera));
            _aoCommand.DispatchCompute(_setup1Compute, kernel, _tiledDepthBuffer2.width, _tiledDepthBuffer2.height, 1);

            // 2nd downsampling pass.
            kernel = _setup2Compute.FindKernel("main");
            _aoCommand.SetComputeTextureParam(_setup2Compute, kernel, "DS4x", _downsizedDepthBuffer2);
            _aoCommand.SetComputeTextureParam(_setup2Compute, kernel, "DS8x", _downsizedDepthBuffer3);
            _aoCommand.SetComputeTextureParam(_setup2Compute, kernel, "DS16x", _downsizedDepthBuffer4);
            _aoCommand.SetComputeTextureParam(_setup2Compute, kernel, "DS8xAtlas", _tiledDepthBuffer3);
            _aoCommand.SetComputeTextureParam(_setup2Compute, kernel, "DS16xAtlas", _tiledDepthBuffer4);
            _aoCommand.DispatchCompute(_setup2Compute, kernel, _tiledDepthBuffer4.width, _tiledDepthBuffer4.height, 1);
        }

        void AddAOCommands(CommandBuffer cmd, Camera camera)
        {
        }

        void AddDebugCommands(CommandBuffer cmd)
        {
            //_debugCommand.SetGlobalTexture("_AOTexture", _aoBuffer);
            //_debugCommand.Blit(null, BuiltinRenderTextureType.CurrentActive, _debugMaterial, 0);
            //_debugCommand.SetGlobalTexture("_AOTexture", _downsizedDepthBuffer4);
            //_debugCommand.Blit(null, BuiltinRenderTextureType.CurrentActive, _debugMaterial, 0);
            _debugCommand.SetGlobalTexture("_TileTexture", _tiledDepthBuffer4);
            _debugCommand.Blit(null, BuiltinRenderTextureType.CurrentActive, _debugMaterial, 1);
        }

        #endregion

#if lsjdldfjksla














            // Execute it before image effects.

        #region Private functions
            uint sizeX, sizeY, sizeZ;
            _compute.GetKernelThreadGroupSizes(kernel, out sizeX, out sizeY, out sizeZ);

            _aoCommand.DispatchCompute(
                _compute, kernel,
                camera.pixelWidth / (int)sizeX,
                camera.pixelHeight / (int)sizeY,
                1
            );
            var kernel = _compute.FindKernel("main");

            _aoCommand.SetComputeTextureParam(
                _compute, kernel, "DepthTex", BuiltinRenderTextureType.ResolvedDepth
            );

            _aoCommand.SetComputeTextureParam(
                _compute, kernel, "Occlusion", _aoBuffer
            );

            UpdateComputeParameters(camera.pixelWidth, camera.pixelHeight, 1 / camera.projectionMatrix[0, 0]);

        void UpdateComputeParameters(float width, float height, float TanHalfFovH)
        {
            var SampleThickness = new [] {
                Mathf.Sqrt(1.0f - 0.2f * 0.2f),
                Mathf.Sqrt(1.0f - 0.4f * 0.4f),
                Mathf.Sqrt(1.0f - 0.6f * 0.6f),
                Mathf.Sqrt(1.0f - 0.8f * 0.8f),
                Mathf.Sqrt(1.0f - 0.2f * 0.2f - 0.2f * 0.2f),
                Mathf.Sqrt(1.0f - 0.2f * 0.2f - 0.4f * 0.4f),
                Mathf.Sqrt(1.0f - 0.2f * 0.2f - 0.6f * 0.6f),
                Mathf.Sqrt(1.0f - 0.2f * 0.2f - 0.8f * 0.8f),
                Mathf.Sqrt(1.0f - 0.4f * 0.4f - 0.4f * 0.4f),
                Mathf.Sqrt(1.0f - 0.4f * 0.4f - 0.6f * 0.6f),
                Mathf.Sqrt(1.0f - 0.4f * 0.4f - 0.8f * 0.8f),
                Mathf.Sqrt(1.0f - 0.6f * 0.6f - 0.6f * 0.6f)
            };

            // Here we compute multipliers that convert the center depth value into (the reciprocal of)
            // sphere thicknesses at each sample location.  This assumes a maximum sample radius of 5
            // units, but since a sphere has no thickness at its extent, we don't need to sample that far
            // out.  Only samples whole integer offsets with distance less than 25 are used.  This means
            // that there is no sample at (3, 4) because its distance is exactly 25 (and has a thickness of 0.)

            // The shaders are set up to sample a circular region within a 5-pixel radius.
            const float ScreenspaceDiameter = 10.0f;

            // SphereDiameter = CenterDepth * ThicknessMultiplier.  This will compute the thickness of a sphere centered
            // at a specific depth.  The ellipsoid scale can stretch a sphere into an ellipsoid, which changes the
            // characteristics of the AO.
            // TanHalfFovH:  Radius of sphere in depth units if its center lies at Z = 1
            // ScreenspaceDiameter:  Diameter of sample sphere in pixel units
            // ScreenspaceDiameter / BufferWidth:  Ratio of the screen width that the sphere actually covers
            // Note about the "2.0f * ":  Diameter = 2 * Radius
            var ThicknessMultiplier = 2.0f * TanHalfFovH * ScreenspaceDiameter / width;
            ThicknessMultiplier *= 2.0f;

            // This will transform a depth value from [0, thickness] to [0, 1].
            var InverseRangeFactor = 1.0f / ThicknessMultiplier;

            // The thicknesses are smaller for all off-center samples of the sphere.  Compute thicknesses relative
            // to the center sample.
            var InvThicknessTable = new [] {
                InverseRangeFactor / SampleThickness[ 0],
                InverseRangeFactor / SampleThickness[ 1],
                InverseRangeFactor / SampleThickness[ 2],
                InverseRangeFactor / SampleThickness[ 3],
                InverseRangeFactor / SampleThickness[ 4],
                InverseRangeFactor / SampleThickness[ 5],
                InverseRangeFactor / SampleThickness[ 6],
                InverseRangeFactor / SampleThickness[ 7],
                InverseRangeFactor / SampleThickness[ 8],
                InverseRangeFactor / SampleThickness[ 9],
                InverseRangeFactor / SampleThickness[10],
                InverseRangeFactor / SampleThickness[11]
            };

            // These are the weights that are multiplied against the samples because not all samples are
            // equally important.  The farther the sample is from the center location, the less they matter.
            // We use the thickness of the sphere to determine the weight.  The scalars in front are the number
            // of samples with this weight because we sum the samples together before multiplying by the weight,
            // so as an aggregate all of those samples matter more.  After generating this table, the weights
            // are normalized.
            var SampleWeightTable = new [] {
                4.0f * SampleThickness[ 0],    // Axial
                4.0f * SampleThickness[ 1],    // Axial
                4.0f * SampleThickness[ 2],    // Axial
                4.0f * SampleThickness[ 3],    // Axial
                4.0f * SampleThickness[ 4],    // Diagonal
                8.0f * SampleThickness[ 5],    // L-shaped
                8.0f * SampleThickness[ 6],    // L-shaped
                8.0f * SampleThickness[ 7],    // L-shaped
                4.0f * SampleThickness[ 8],    // Diagonal
                8.0f * SampleThickness[ 9],    // L-shaped
                8.0f * SampleThickness[10],    // L-shaped
                4.0f * SampleThickness[11]     // Diagonal
            };

            SampleWeightTable[0] = 0;
            SampleWeightTable[2] = 0;
            SampleWeightTable[5] = 0;
            SampleWeightTable[7] = 0;
            SampleWeightTable[9] = 0;

            // Normalize the weights by dividing by the sum of all weights
            var totalWeight = 0.0f;

            foreach (var w in SampleWeightTable)
                totalWeight += w;

            for (var i = 0; i < SampleWeightTable.Length; i++)
                SampleWeightTable[i] /= totalWeight;

            _aoCommand.SetComputeFloatParams(_compute, "gInvThicknessTable", InvThicknessTable);
            _aoCommand.SetComputeFloatParams(_compute, "gSampleWeightTable", SampleWeightTable);
            _aoCommand.SetComputeFloatParam(_compute, "gInvSliceWidth", 1 / width);
            _aoCommand.SetComputeFloatParam(_compute, "gInvSliceHeight", 1 / height);
            _aoCommand.SetComputeFloatParam(_compute, "gRejectFadeoff", 1 / -_rejectionFalloff);
            _aoCommand.SetComputeFloatParam(_compute, "gRcpAccentuation", 1 / (1 + _accentuation));

        }


        #endregion
#endif
    }
}
