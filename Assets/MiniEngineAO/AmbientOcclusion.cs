// MiniEngine SSAO for Unity
// https://github.com/keijiro/MiniEngineAO

using UnityEngine;
using UnityEngine.Rendering;

namespace MiniEngineAO
{
    [RequireComponent(typeof(Camera))]
    public sealed class AmbientOcclusion : MonoBehaviour
    {
        #region Exposed attributes

        [SerializeField, Range(-8, 0)] float _noiseFilterTolerance = -3;

        public float noiseFilterTolerance
        {
            get { return _noiseFilterTolerance; }
            set { UpdateProperty(ref _noiseFilterTolerance, value); }
        }

        [SerializeField, Range(-8, -1)] float _blurTolerance = -5;

        public float blurTolerance
        {
            get { return _blurTolerance; }
            set { UpdateProperty(ref _blurTolerance, value); }
        }

        [SerializeField, Range(-12, -1)] float _upsampleTolerance = -7;

        public float upsampleTolerance
        {
            get { return _upsampleTolerance; }
            set { UpdateProperty(ref _upsampleTolerance, value); }
        }

        [SerializeField, Range(1, 10)] float _rejectionFalloff = 2.5f;

        public float rejectionFalloff
        {
            get { return _rejectionFalloff; }
            set { UpdateProperty(ref _rejectionFalloff, value); }
        }

        [SerializeField, Range(0, 1)] float _accentuation = 0.1f;

        public float accentuation
        {
            get { return _accentuation; }
            set { UpdateProperty(ref _accentuation, value); }
        }

        #endregion

        #region Built-in resources

        [SerializeField, HideInInspector] ComputeShader _downsample1Compute;
        [SerializeField, HideInInspector] ComputeShader _downsample2Compute;
        [SerializeField, HideInInspector] ComputeShader _renderCompute;
        [SerializeField, HideInInspector] ComputeShader _upsampleCompute;
        [SerializeField, HideInInspector] Shader _utilShader;

        #endregion

        #region Hash IDs

        internal static class ShaderIDs
        {
            public static readonly int DepthCopy = Shader.PropertyToID("DepthCopy");
            public static readonly int LinearDepth = Shader.PropertyToID("LinearDepth");
            public static readonly int LowDepth1 = Shader.PropertyToID("LowDepth1");
            public static readonly int LowDepth2 = Shader.PropertyToID("LowDepth2");
            public static readonly int LowDepth3 = Shader.PropertyToID("LowDepth3");
            public static readonly int LowDepth4 = Shader.PropertyToID("LowDepth4");
            public static readonly int Occlusion1 = Shader.PropertyToID("Occlusion1");
            public static readonly int Occlusion2 = Shader.PropertyToID("Occlusion2");
            public static readonly int Occlusion3 = Shader.PropertyToID("Occlusion3");
            public static readonly int Occlusion4 = Shader.PropertyToID("Occlusion4");
            public static readonly int Composite1 = Shader.PropertyToID("Composite1");
            public static readonly int Composite2 = Shader.PropertyToID("Composite2");
            public static readonly int Composite3 = Shader.PropertyToID("Composite3");
        }

        #endregion

        #region Temporary objects

        RenderTexture _tiledDepthBuffer1;
        RenderTexture _tiledDepthBuffer2;
        RenderTexture _tiledDepthBuffer3;
        RenderTexture _tiledDepthBuffer4;
        RenderTexture _resultBuffer;

        CommandBuffer _renderCommand;
        CommandBuffer _debugCommand;

        Material _utilMaterial;

        #endregion

        #region Private utility methods

        void UpdateProperty(ref float prop, float value)
        {
            if (prop == value) return;
            _noiseFilterTolerance = value;
            UpdateCommandBuffer();
        }

        bool CheckIfResolvedDepthAvailable()
        {
            // AFAIK, resolved depth is only available on D3D11/12.
            // TODO: Is there more proper way to determine this?
            var rpath = GetComponent<Camera>().actualRenderingPath;
            var gtype = SystemInfo.graphicsDeviceType;
            return rpath == RenderingPath.DeferredShading &&
                   (gtype == GraphicsDeviceType.Direct3D11 ||
                    gtype == GraphicsDeviceType.Direct3D12);
        }

        Vector2 BufferDimensionVector(int downscale)
        {
            return new Vector2(
                (_resultBuffer.width  + (downscale - 1)) / downscale,
                (_resultBuffer.height + (downscale - 1)) / downscale
            );
        }

        Vector2 InverseDimensionVector(int downscale)
        {
            var v = BufferDimensionVector(downscale);
            return new Vector2(1.0f / v.x, 1.0f / v.y);
        }

        // Calculate values in _ZBuferParams (built-in shader variable)
        // We can't use _ZBufferParams in compute shaders, so this function is
        // used to give the values in it to compute shaders.
        static Vector4 CalculateZBufferParams(Camera camera)
        {
            var fpn = camera.farClipPlane / camera.nearClipPlane;
            if (SystemInfo.usesReversedZBuffer)
                return new Vector4(fpn - 1, 1, 0, 0);
            else
                return new Vector4(1 - fpn, fpn, 0, 0);
        }

        static void SetupUAVBuffer(ref RenderTexture rt, int width, int height, int depth, int bpp)
        {
            // Do nothing and return if the RT has been alreadly allocated and
            // the dimensions are unchanged.
            if (rt != null && rt.width == width && rt.height == height) return;

            // Convert BPP value to RenderTextureFormat.
            var format = RenderTextureFormat.R8;
            if (bpp == 16) format = RenderTextureFormat.RHalf;
            if (bpp == 32) format = RenderTextureFormat.RFloat;

            // (Re)allocate the RT.
            DestroyIfAlive(rt);
            rt = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
            rt.enableRandomWrite = true;

            // Change it to a texture array if a depth value is given.
            if (depth > 1)
            {
                rt.dimension = TextureDimension.Tex2DArray;
                rt.volumeDepth = depth;
            }

            rt.Create();
        }

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
            GetComponent<Camera>().depthTextureMode = DepthTextureMode.Depth;

            _renderCommand = new CommandBuffer();
            _renderCommand.name = "SSAO";

            _debugCommand = new CommandBuffer();
            _debugCommand.name = "SSAO Debug";

            _utilMaterial = new Material(_utilShader);

            UpdateCommandBuffer();
        }

        void OnEnable()
        {
            if (_renderCommand != null) RegisterCommandBuffers();
        }

        void OnDisable()
        {
            if (_renderCommand != null) UnregisterCommandBuffers();
        }

        void OnDestroy()
        {
            DestroyIfAlive(_tiledDepthBuffer1);
            DestroyIfAlive(_tiledDepthBuffer2);
            DestroyIfAlive(_tiledDepthBuffer3);
            DestroyIfAlive(_tiledDepthBuffer4);
            DestroyIfAlive(_resultBuffer);

            _renderCommand.Dispose();
            _debugCommand.Dispose();

            DestroyIfAlive(_utilMaterial);
        }

        #endregion

        #region Array arguments for the render kernel

        // These arrays are reused between frames to reduce GC allocation.

        static readonly float [] SampleThickness = {
            Mathf.Sqrt(1 - 0.2f * 0.2f),
            Mathf.Sqrt(1 - 0.4f * 0.4f),
            Mathf.Sqrt(1 - 0.6f * 0.6f),
            Mathf.Sqrt(1 - 0.8f * 0.8f),
            Mathf.Sqrt(1 - 0.2f * 0.2f - 0.2f * 0.2f),
            Mathf.Sqrt(1 - 0.2f * 0.2f - 0.4f * 0.4f),
            Mathf.Sqrt(1 - 0.2f * 0.2f - 0.6f * 0.6f),
            Mathf.Sqrt(1 - 0.2f * 0.2f - 0.8f * 0.8f),
            Mathf.Sqrt(1 - 0.4f * 0.4f - 0.4f * 0.4f),
            Mathf.Sqrt(1 - 0.4f * 0.4f - 0.6f * 0.6f),
            Mathf.Sqrt(1 - 0.4f * 0.4f - 0.8f * 0.8f),
            Mathf.Sqrt(1 - 0.6f * 0.6f - 0.6f * 0.6f)
        };

        static float [] InvThicknessTable = new float [12];
        static float [] SampleWeightTable = new float [12];

        #endregion

        #region Command buffer builders

        void RegisterCommandBuffers()
        {
            var camera = GetComponent<Camera>();
            camera.AddCommandBuffer(CameraEvent.BeforeLighting, _renderCommand);
            camera.AddCommandBuffer(CameraEvent.BeforeLighting, _debugCommand);
        }

        void UnregisterCommandBuffers()
        {
            var camera = GetComponent<Camera>();
            camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, _renderCommand);
            camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, _debugCommand);
        }

        public void UpdateCommandBuffer()
        {
            // Remove the old commands from the camera.
            if (_renderCommand != null) UnregisterCommandBuffers();

            // Buffer reallocation.
            var camera = GetComponent<Camera>();
            SetupBuffers(camera.pixelWidth, camera.pixelHeight);

            // Rebuild the render commands.
            _renderCommand.Clear();

            AddDownsampleCommands(_renderCommand, CalculateZBufferParams(camera));

            AddBufferAllocationCommand(_renderCommand, ShaderIDs.Occlusion1, 2, 8, true);
            AddBufferAllocationCommand(_renderCommand, ShaderIDs.Occlusion2, 4, 8, true);
            AddBufferAllocationCommand(_renderCommand, ShaderIDs.Occlusion3, 8, 8, true);
            AddBufferAllocationCommand(_renderCommand, ShaderIDs.Occlusion4, 16, 8, true);

            var tanHalfFovH = 1 / camera.projectionMatrix[0, 0];
            AddRenderCommands(_renderCommand, _tiledDepthBuffer1, ShaderIDs.Occlusion1, 8, tanHalfFovH);
            AddRenderCommands(_renderCommand, _tiledDepthBuffer2, ShaderIDs.Occlusion2, 16, tanHalfFovH);
            AddRenderCommands(_renderCommand, _tiledDepthBuffer3, ShaderIDs.Occlusion3, 32, tanHalfFovH);
            AddRenderCommands(_renderCommand, _tiledDepthBuffer4, ShaderIDs.Occlusion4, 64, tanHalfFovH);

            AddBufferAllocationCommand(_renderCommand, ShaderIDs.Composite1, 2, 8, true);
            AddBufferAllocationCommand(_renderCommand, ShaderIDs.Composite2, 4, 8, true);
            AddBufferAllocationCommand(_renderCommand, ShaderIDs.Composite3, 8, 8, true);

            AddUpsampleCommands(_renderCommand, ShaderIDs.LowDepth4, ShaderIDs.Occlusion4, 16, ShaderIDs.LowDepth3, ShaderIDs.Occlusion3, 8, ShaderIDs.Composite3);
            AddUpsampleCommands(_renderCommand, ShaderIDs.LowDepth3, ShaderIDs.Composite3,  8, ShaderIDs.LowDepth2, ShaderIDs.Occlusion2, 4, ShaderIDs.Composite2);
            AddUpsampleCommands(_renderCommand, ShaderIDs.LowDepth2, ShaderIDs.Composite2,  4, ShaderIDs.LowDepth1, ShaderIDs.Occlusion1, 2, ShaderIDs.Composite1);
            AddUpsampleCommands(_renderCommand, ShaderIDs.LowDepth1, ShaderIDs.Composite1,  2, ShaderIDs.LinearDepth, -1, 1, -1);

            // Rebuild the debug commands.
            _debugCommand.Clear();
            AddDebugCommands(_debugCommand);

            // Add the updated commands to the camera.
            RegisterCommandBuffers();
        }

        void SetupBuffers(int width, int height)
        {
            //var w1 = (width +  1) /  2; var h1 = (height +  1) /  2;
            //var w2 = (width +  3) /  4; var h2 = (height +  3) /  4;
            var w3 = (width +  7) /  8; var h3 = (height +  7) /  8;
            var w4 = (width + 15) / 16; var h4 = (height + 15) / 16;
            var w5 = (width + 31) / 32; var h5 = (height + 31) / 32;
            var w6 = (width + 63) / 64; var h6 = (height + 63) / 64;

            //SetupUAVBuffer(ref _linearDepthBuffer, width, height, 1, 16);

            //SetupUAVBuffer(ref _lowDepthBuffer1, w1, h1, 1, 32);
            //SetupUAVBuffer(ref _lowDepthBuffer2, w2, h2, 1, 32);
            //SetupUAVBuffer(ref _lowDepthBuffer3, w3, h3, 1, 32);
            //SetupUAVBuffer(ref _lowDepthBuffer4, w4, h4, 1, 32);

            SetupUAVBuffer(ref _tiledDepthBuffer1, w3, h3, 16, 16);
            SetupUAVBuffer(ref _tiledDepthBuffer2, w4, h4, 16, 16);
            SetupUAVBuffer(ref _tiledDepthBuffer3, w5, h5, 16, 16);
            SetupUAVBuffer(ref _tiledDepthBuffer4, w6, h6, 16, 16);

            //SetupUAVBuffer(ref _renderBuffer1, w1, h1, 1, 8);
            //SetupUAVBuffer(ref _renderBuffer2, w2, h2, 1, 8);
            //SetupUAVBuffer(ref _renderBuffer3, w3, h3, 1, 8);
            //SetupUAVBuffer(ref _renderBuffer4, w4, h4, 1, 8);

            //SetupUAVBuffer(ref _blurBuffer1, w1, h1, 1, 8);
            //SetupUAVBuffer(ref _blurBuffer2, w2, h2, 1, 8);
            //SetupUAVBuffer(ref _blurBuffer3, w3, h3, 1, 8);

            SetupUAVBuffer(ref _resultBuffer, width, height, 1, 8);
        }

        void AddBufferAllocationCommand(CommandBuffer cmd, int id, int downscale, int bpp, bool uav)
        {
            var dim = BufferDimensionVector(downscale);

            var format = RenderTextureFormat.R8;
            if (bpp == 16) format = RenderTextureFormat.RHalf;
            if (bpp == 32) format = RenderTextureFormat.RFloat;

            cmd.GetTemporaryRT(
                id, (int)dim.x, (int)dim.y, 0,
                FilterMode.Point, format,
                RenderTextureReadWrite.Linear, 1, uav
            );
        }

        void AddDownsampleCommands(CommandBuffer cmd, Vector4 zBufferParams)
        {
            // Buffer allocations.
            var copyDepth = !CheckIfResolvedDepthAvailable();

            if (copyDepth)
            {
                AddBufferAllocationCommand(cmd, ShaderIDs.DepthCopy, 1, 32, false);
                _renderCommand.Blit(null, ShaderIDs.DepthCopy, _utilMaterial, 0);
            }

            AddBufferAllocationCommand(cmd, ShaderIDs.LinearDepth, 1, 16, true);
            AddBufferAllocationCommand(cmd, ShaderIDs.LowDepth1, 2, 32, true);
            AddBufferAllocationCommand(cmd, ShaderIDs.LowDepth2, 4, 32, true);
            AddBufferAllocationCommand(cmd, ShaderIDs.LowDepth3, 8, 32, true);
            AddBufferAllocationCommand(cmd, ShaderIDs.LowDepth4, 16, 32, true);

            // 1st downsampling pass.
            var cs = _downsample1Compute;
            var kernel = cs.FindKernel("main");
            _renderCommand.SetComputeTextureParam(cs, kernel, "LinearZ", ShaderIDs.LinearDepth);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS2x", ShaderIDs.LowDepth1);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS4x", ShaderIDs.LowDepth2);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS2xAtlas", _tiledDepthBuffer1);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS4xAtlas", _tiledDepthBuffer2);
            if (copyDepth)
                _renderCommand.SetComputeTextureParam(cs, kernel, "Depth", ShaderIDs.DepthCopy);
            else
                _renderCommand.SetComputeTextureParam(cs, kernel, "Depth", BuiltinRenderTextureType.ResolvedDepth);
            _renderCommand.SetComputeVectorParam(cs, "ZBufferParams", zBufferParams);
            _renderCommand.DispatchCompute(cs, kernel, _tiledDepthBuffer2.width, _tiledDepthBuffer2.height, 1);

            if (copyDepth) _renderCommand.ReleaseTemporaryRT(ShaderIDs.DepthCopy);

            // 2nd downsampling pass.
            cs = _downsample2Compute;
            kernel = cs.FindKernel("main");
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS4x", ShaderIDs.LowDepth2);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS8x", ShaderIDs.LowDepth3);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS16x", ShaderIDs.LowDepth4);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS8xAtlas", _tiledDepthBuffer3);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS16xAtlas", _tiledDepthBuffer4);
            _renderCommand.DispatchCompute(cs, kernel, _tiledDepthBuffer4.width, _tiledDepthBuffer4.height, 1);
        }

        void AddRenderCommands(CommandBuffer cmd, RenderTexture depthBuffer, int outBuffer, int downscale, float TanHalfFovH)
        {
            // Here we compute multipliers that convert the center depth value into (the reciprocal of)
            // sphere thicknesses at each sample location.  This assumes a maximum sample radius of 5
            // units, but since a sphere has no thickness at its extent, we don't need to sample that far
            // out.  Only samples whole integer offsets with distance less than 25 are used.  This means
            // that there is no sample at (3, 4) because its distance is exactly 25 (and has a thickness of 0.)

            // The shaders are set up to sample a circular region within a 5-pixel radius.
            const float ScreenspaceDiameter = 10;

            // SphereDiameter = CenterDepth * ThicknessMultiplier.  This will compute the thickness of a sphere centered
            // at a specific depth.  The ellipsoid scale can stretch a sphere into an ellipsoid, which changes the
            // characteristics of the AO.
            // TanHalfFovH:  Radius of sphere in depth units if its center lies at Z = 1
            // ScreenspaceDiameter:  Diameter of sample sphere in pixel units
            // ScreenspaceDiameter / BufferWidth:  Ratio of the screen width that the sphere actually covers
            // Note about the "2.0f * ":  Diameter = 2 * Radius
            var ThicknessMultiplier = 2 * TanHalfFovH * ScreenspaceDiameter / depthBuffer.width;
            if (depthBuffer.volumeDepth == 1) ThicknessMultiplier *= 2;

            // This will transform a depth value from [0, thickness] to [0, 1].
            var InverseRangeFactor = 1 / ThicknessMultiplier;

            // The thicknesses are smaller for all off-center samples of the sphere.  Compute thicknesses relative
            // to the center sample.
            for (var i = 0; i < 12; i++)
                InvThicknessTable[i] = InverseRangeFactor / SampleThickness[i];

            // These are the weights that are multiplied against the samples because not all samples are
            // equally important.  The farther the sample is from the center location, the less they matter.
            // We use the thickness of the sphere to determine the weight.  The scalars in front are the number
            // of samples with this weight because we sum the samples together before multiplying by the weight,
            // so as an aggregate all of those samples matter more.  After generating this table, the weights
            // are normalized.
            SampleWeightTable[ 0] = 4 * SampleThickness[ 0];    // Axial
            SampleWeightTable[ 1] = 4 * SampleThickness[ 1];    // Axial
            SampleWeightTable[ 2] = 4 * SampleThickness[ 2];    // Axial
            SampleWeightTable[ 3] = 4 * SampleThickness[ 3];    // Axial
            SampleWeightTable[ 4] = 4 * SampleThickness[ 4];    // Diagonal
            SampleWeightTable[ 5] = 8 * SampleThickness[ 5];    // L-shaped
            SampleWeightTable[ 6] = 8 * SampleThickness[ 6];    // L-shaped
            SampleWeightTable[ 7] = 8 * SampleThickness[ 7];    // L-shaped
            SampleWeightTable[ 8] = 4 * SampleThickness[ 8];    // Diagonal
            SampleWeightTable[ 9] = 8 * SampleThickness[ 9];    // L-shaped
            SampleWeightTable[10] = 8 * SampleThickness[10];    // L-shaped
            SampleWeightTable[11] = 4 * SampleThickness[11];    // Diagonal

            // Zero out the unused samples.
            // FIXME: should we support SAMPLE_EXHAUSTIVELY mode?
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

            // Set the arguments for the render kernel.
            var kernel = _renderCompute.FindKernel("main_interleaved");
            _renderCommand.SetComputeFloatParams(_renderCompute, "gInvThicknessTable", InvThicknessTable);
            _renderCommand.SetComputeFloatParams(_renderCompute, "gSampleWeightTable", SampleWeightTable);
            _renderCommand.SetComputeVectorParam(_renderCompute, "gInvSliceDimension", InverseDimensionVector(downscale));
            _renderCommand.SetComputeFloatParam(_renderCompute, "gRejectFadeoff", -1 / _rejectionFalloff);
            _renderCommand.SetComputeFloatParam(_renderCompute, "gRcpAccentuation", 1 / (1 + _accentuation));
            _renderCommand.SetComputeTextureParam(_renderCompute, kernel, "DepthTex", depthBuffer);
            _renderCommand.SetComputeTextureParam(_renderCompute, kernel, "Occlusion", outBuffer);

            // Calculate the thread group count and add a dispatch command with them.
            uint xsize, ysize, zsize;
            _renderCompute.GetKernelThreadGroupSizes(kernel, out xsize, out ysize, out zsize);

            var xcount = (depthBuffer.width       + (int)xsize - 1) / (int)xsize;
            var ycount = (depthBuffer.height      + (int)ysize - 1) / (int)ysize;
            var zcount = (depthBuffer.volumeDepth + (int)zsize - 1) / (int)zsize;

            _renderCommand.DispatchCompute(_renderCompute, kernel, xcount, ycount, zcount);
        }

        void AddUpsampleCommands(
            CommandBuffer cmd,
            int lowResDepth, int interleavedAO, int lowResDownscale,
            int highResDepth, int highResAO, int highResDownscale,
            int destination
        )
        {
            var kernelName = (highResAO < 0) ? "main" : "main_blendout";
            var kernel = _upsampleCompute.FindKernel(kernelName);

            var stepSize = 1920.0f / BufferDimensionVector(lowResDownscale).x;
            var blurTolerance = 1 - Mathf.Pow(10, _blurTolerance) * stepSize;
            blurTolerance *= blurTolerance;
            var upsampleTolerance = Mathf.Pow(10, _upsampleTolerance);
            var noiseFilterWeight = 1 / (Mathf.Pow(10, _noiseFilterTolerance) + upsampleTolerance);

            _renderCommand.SetComputeVectorParam(_upsampleCompute, "InvLowResolution", InverseDimensionVector(lowResDownscale));
            _renderCommand.SetComputeVectorParam(_upsampleCompute, "InvHighResolution", InverseDimensionVector(highResDownscale));
            _renderCommand.SetComputeFloatParam(_upsampleCompute, "NoiseFilterStrength", noiseFilterWeight);
            _renderCommand.SetComputeFloatParam(_upsampleCompute, "StepSize", stepSize);
            _renderCommand.SetComputeFloatParam(_upsampleCompute, "kBlurTolerance", blurTolerance);
            _renderCommand.SetComputeFloatParam(_upsampleCompute, "kUpsampleTolerance", upsampleTolerance);

            _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "LoResDB", lowResDepth);
            _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "HiResDB", highResDepth);
            _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "LoResAO1", interleavedAO);
            _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "HiResAO", highResAO);
            if (destination < 0)
                _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "AoResult", _resultBuffer);
            else
                _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "AoResult", destination);

            var highResDim = BufferDimensionVector(highResDownscale);
            var xcount = ((int)highResDim.x + 17) / 16;
            var ycount = ((int)highResDim.y + 17) / 16;
            _renderCommand.DispatchCompute(_upsampleCompute, kernel, xcount, ycount, 1);
        }

        void AddDebugCommands(CommandBuffer cmd)
        {
            _debugCommand.SetGlobalTexture("_AOTexture", _resultBuffer);
            _debugCommand.Blit(null, BuiltinRenderTextureType.CurrentActive, _utilMaterial, 1);
        }

        #endregion
    }
}
