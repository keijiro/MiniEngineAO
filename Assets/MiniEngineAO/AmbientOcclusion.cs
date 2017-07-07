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

        #region Internal classes

        internal enum MipLevel { Original, L1, L2, L3, L4, L5, L6 }

        internal enum BufferType
        {
            Fixed, Half, Float,
            FixedUAV, HalfUAV, FloatUAV,
            FixedTileUAV, HalfTileUAV, FloatTileUAV
        }

        // A wrapper class used for handling internal render textures. This
        // provides a common interface for allocated RTs and temporary RTs
        // (to be allocated within a command buffer).
        internal class RTBuffer
        {
            // Base dimensions
            static int _baseWidth;
            static int _baseHeight;

            public static void SetBaseDimensions(int w, int h)
            {
                _baseWidth = w;
                _baseHeight = h;
            }

            // Public properties
            public int nameID { get { return _id; } }
            public int width { get { return _width; } }
            public int height { get { return _height; } }
            public bool isTiled { get { return (int)_type > 5; } }
            public bool hasUAV { get { return (int)_type > 2; } }

            public Vector2 inverseDimensions
            {
                get { return new Vector2(1.0f / width, 1.0f / height); }
            }

            public RenderTargetIdentifier id
            {
                get
                {
                    if (_rt != null)
                        return new RenderTargetIdentifier(_rt);
                    else
                        return new RenderTargetIdentifier(_id);
                }
            }

            // Constructor
            public RTBuffer(string name, BufferType type, MipLevel level)
            {
                _id = Shader.PropertyToID(name);
                _type = type;
                _level = level;
            }

            // Allocate the buffer in advance.
            public void Allocate()
            {
                ResetDimensions();

                if (_rt == null)
                {
                    // Initial allocation.
                    _rt = new RenderTexture(
                        _width, _height, 0,
                        renderTextureFormat,
                        RenderTextureReadWrite.Linear
                    );
                }
                else
                {
                    // Release and reallocate.
                    _rt.Release();
                    _rt.width = _width;
                    _rt.height = _height;
                    _rt.format = renderTextureFormat;
                }

                _rt.enableRandomWrite = hasUAV;

                // Should it be tiled?
                if (isTiled)
                {
                    _rt.dimension = TextureDimension.Tex2DArray;
                    _rt.volumeDepth = 16;
                }

                _rt.Create();
            }

            // Add an allocation command to the given command buffer.
            public void AddAllocationCommand(CommandBuffer cmd)
            {
                ResetDimensions();

                cmd.GetTemporaryRT(
                    _id, _width, _height, 0,
                    FilterMode.Point, renderTextureFormat,
                    RenderTextureReadWrite.Linear, 1, hasUAV
                );
            }

            // Destroy internal resources.
            public void Destroy()
            {
                if (_rt != null)
                {
                    if (Application.isPlaying)
                        RenderTexture.Destroy(_rt);
                    else
                        RenderTexture.DestroyImmediate(_rt);
                }
            }

            // Private variables
            int _id;
            RenderTexture _rt;
            int _width, _height;
            BufferType _type;
            MipLevel _level;

            // Determine the render texture format.
            RenderTextureFormat renderTextureFormat
            {
                get
                {
                    switch ((int)_type % 3)
                    {
                        case 0: return RenderTextureFormat.R8;
                        case 1: return RenderTextureFormat.RHalf;
                        default: return RenderTextureFormat.RFloat;
                    }
                }
            }

            // (Re)calculate the texture width and height.
            void ResetDimensions()
            {
                var div = 1 << (int)_level;
                _width  = (_baseWidth  + (div - 1)) / div;
                _height = (_baseHeight + (div - 1)) / div;
            }
        }

        #endregion

        #region Internal objects

        RTBuffer _depthCopy;
        RTBuffer _linearDepth;
        RTBuffer _lowDepth1;
        RTBuffer _lowDepth2;
        RTBuffer _lowDepth3;
        RTBuffer _lowDepth4;
        RTBuffer _tiledDepth1;
        RTBuffer _tiledDepth2;
        RTBuffer _tiledDepth3;
        RTBuffer _tiledDepth4;
        RTBuffer _occlusion1;
        RTBuffer _occlusion2;
        RTBuffer _occlusion3;
        RTBuffer _occlusion4;
        RTBuffer _composite1;
        RTBuffer _composite2;
        RTBuffer _composite3;
        RTBuffer _result;

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

        #endregion

        #region MonoBehaviour functions

        void Start()
        {
            // Determine the base dimensions of the render buffers.
            var camera = GetComponent<Camera>();
            RTBuffer.SetBaseDimensions(camera.pixelWidth, camera.pixelHeight);

            // We requires the camera depth texture.
            camera.depthTextureMode = DepthTextureMode.Depth;

            // Local buffer declarations.
            _depthCopy = new RTBuffer("DepthCopy", BufferType.Float, MipLevel.Original);
            _linearDepth = new RTBuffer("LinearDepth", BufferType.HalfUAV, MipLevel.Original);

            _lowDepth1 = new RTBuffer("LowDepth1", BufferType.FloatUAV, MipLevel.L1);
            _lowDepth2 = new RTBuffer("LowDepth2", BufferType.FloatUAV, MipLevel.L2);
            _lowDepth3 = new RTBuffer("LowDepth3", BufferType.FloatUAV, MipLevel.L3);
            _lowDepth4 = new RTBuffer("LowDepth4", BufferType.FloatUAV, MipLevel.L4);

            _tiledDepth1 = new RTBuffer("TiledDepth1", BufferType.HalfTileUAV, MipLevel.L3);
            _tiledDepth2 = new RTBuffer("TiledDepth2", BufferType.HalfTileUAV, MipLevel.L4);
            _tiledDepth3 = new RTBuffer("TiledDepth3", BufferType.HalfTileUAV, MipLevel.L5);
            _tiledDepth4 = new RTBuffer("TiledDepth4", BufferType.HalfTileUAV, MipLevel.L6);

            _occlusion1 = new RTBuffer("Occlusion1", BufferType.FixedUAV, MipLevel.L1);
            _occlusion2 = new RTBuffer("Occlusion2", BufferType.FixedUAV, MipLevel.L2);
            _occlusion3 = new RTBuffer("Occlusion3", BufferType.FixedUAV, MipLevel.L3);
            _occlusion4 = new RTBuffer("Occlusion4", BufferType.FixedUAV, MipLevel.L4);

            _composite1 = new RTBuffer("Composite1", BufferType.FixedUAV, MipLevel.L1);
            _composite2 = new RTBuffer("Composite2", BufferType.FixedUAV, MipLevel.L2);
            _composite3 = new RTBuffer("Composite3", BufferType.FixedUAV, MipLevel.L3);

            _result = new RTBuffer("AmbientOcclusion", BufferType.FixedUAV, MipLevel.Original);

            // We can't allocate tiled buffers (texture arrays) within
            // a command buffer, so allocate them in advance.
            _tiledDepth1.Allocate();
            _tiledDepth2.Allocate();
            _tiledDepth3.Allocate();
            _tiledDepth4.Allocate();

            // The result buffer is to be reused between frames, so allocate it
            // in advance.
            _result.Allocate();

            // Initialize the command buffers.
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
            _tiledDepth1.Destroy();
            _tiledDepth2.Destroy();
            _tiledDepth3.Destroy();
            _tiledDepth4.Destroy();
            _result.Destroy();

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
            var camera = GetComponent<Camera>();

            // Remove the old commands from the camera.
            if (_renderCommand != null) UnregisterCommandBuffers();

            // Rebuild the render commands.
            _renderCommand.Clear();

            AddDownsampleCommands(_renderCommand, CalculateZBufferParams(camera));

            _occlusion1.AddAllocationCommand(_renderCommand);
            _occlusion2.AddAllocationCommand(_renderCommand);
            _occlusion3.AddAllocationCommand(_renderCommand);
            _occlusion4.AddAllocationCommand(_renderCommand);

            var tanHalfFovH = 1 / camera.projectionMatrix[0, 0];
            AddRenderCommands(_renderCommand, _tiledDepth1, _occlusion1, tanHalfFovH);
            AddRenderCommands(_renderCommand, _tiledDepth2, _occlusion2, tanHalfFovH);
            AddRenderCommands(_renderCommand, _tiledDepth3, _occlusion3, tanHalfFovH);
            AddRenderCommands(_renderCommand, _tiledDepth4, _occlusion4, tanHalfFovH);

            _composite1.AddAllocationCommand(_renderCommand);
            _composite2.AddAllocationCommand(_renderCommand);
            _composite3.AddAllocationCommand(_renderCommand);

            AddUpsampleCommands(_renderCommand, _lowDepth4, _occlusion4, _lowDepth3, _occlusion3, _composite3);
            AddUpsampleCommands(_renderCommand, _lowDepth3, _composite3, _lowDepth2, _occlusion2, _composite2);
            AddUpsampleCommands(_renderCommand, _lowDepth2, _composite2, _lowDepth1, _occlusion1, _composite1);
            AddUpsampleCommands(_renderCommand, _lowDepth1, _composite1, _linearDepth, null, _result);

            // Rebuild the debug commands.
            _debugCommand.Clear();
            AddDebugCommands(_debugCommand);

            // Add the updated commands to the camera.
            RegisterCommandBuffers();
        }

        void AddDownsampleCommands(CommandBuffer cmd, Vector4 zBufferParams)
        {
            // Make a copy of the depth texture, or reuse the resolved depth
            // buffer (it's only available in some specific cases).
            var useDepthCopy = !CheckIfResolvedDepthAvailable();
            if (useDepthCopy)
            {
                _depthCopy.AddAllocationCommand(cmd);
                _renderCommand.Blit(null, _depthCopy.id, _utilMaterial, 0);
            }

            // Buffer allocations.
            _linearDepth.AddAllocationCommand(cmd);
            _lowDepth1.AddAllocationCommand(cmd);
            _lowDepth2.AddAllocationCommand(cmd);
            _lowDepth3.AddAllocationCommand(cmd);
            _lowDepth4.AddAllocationCommand(cmd);

            // 1st downsampling pass.
            var cs = _downsample1Compute;
            var kernel = cs.FindKernel("main");

            _renderCommand.SetComputeTextureParam(cs, kernel, "LinearZ", _linearDepth.id);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS2x", _lowDepth1.id);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS4x", _lowDepth2.id);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS2xAtlas", _tiledDepth1.id);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS4xAtlas", _tiledDepth2.id);
            _renderCommand.SetComputeVectorParam(cs, "ZBufferParams", zBufferParams);

            if (useDepthCopy)
                _renderCommand.SetComputeTextureParam(cs, kernel, "Depth", _depthCopy.id);
            else
                _renderCommand.SetComputeTextureParam(cs, kernel, "Depth", BuiltinRenderTextureType.ResolvedDepth);

            _renderCommand.DispatchCompute(cs, kernel, _tiledDepth2.width, _tiledDepth2.height, 1);

            if (useDepthCopy) _renderCommand.ReleaseTemporaryRT(_depthCopy.nameID);

            // 2nd downsampling pass.
            cs = _downsample2Compute;
            kernel = cs.FindKernel("main");

            _renderCommand.SetComputeTextureParam(cs, kernel, "DS4x", _lowDepth2.id);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS8x", _lowDepth3.id);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS16x", _lowDepth4.id);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS8xAtlas", _tiledDepth3.id);
            _renderCommand.SetComputeTextureParam(cs, kernel, "DS16xAtlas", _tiledDepth4.id);

            _renderCommand.DispatchCompute(cs, kernel, _tiledDepth4.width, _tiledDepth4.height, 1);
        }

        void AddRenderCommands(CommandBuffer cmd, RTBuffer source, RTBuffer dest, float TanHalfFovH)
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
            var ThicknessMultiplier = 2 * TanHalfFovH * ScreenspaceDiameter / source.width;
            if (!source.isTiled) ThicknessMultiplier *= 2;

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
            _renderCommand.SetComputeVectorParam(_renderCompute, "gInvSliceDimension", source.inverseDimensions);
            _renderCommand.SetComputeFloatParam(_renderCompute, "gRejectFadeoff", -1 / _rejectionFalloff);
            _renderCommand.SetComputeFloatParam(_renderCompute, "gRcpAccentuation", 1 / (1 + _accentuation));
            _renderCommand.SetComputeTextureParam(_renderCompute, kernel, "DepthTex", source.id);
            _renderCommand.SetComputeTextureParam(_renderCompute, kernel, "Occlusion", dest.id);

            // Calculate the thread group count and add a dispatch command with them.
            uint xsize, ysize, zsize;
            _renderCompute.GetKernelThreadGroupSizes(kernel, out xsize, out ysize, out zsize);

            var xcount = (source.width  + (int)xsize - 1) / (int)xsize;
            var ycount = (source.height + (int)ysize - 1) / (int)ysize;
            var zcount = ((source.isTiled ? 16 : 1) + (int)zsize - 1) / (int)zsize;

            _renderCommand.DispatchCompute(_renderCompute, kernel, xcount, ycount, zcount);
        }

        void AddUpsampleCommands(
            CommandBuffer cmd,
            RTBuffer lowResDepth, RTBuffer interleavedAO,
            RTBuffer highResDepth, RTBuffer highResAO,
            RTBuffer dest
        )
        {
            var kernelName = (highResAO == null) ? "main" : "main_blendout";
            var kernel = _upsampleCompute.FindKernel(kernelName);

            var stepSize = 1920.0f / lowResDepth.width;
            var blurTolerance = 1 - Mathf.Pow(10, _blurTolerance) * stepSize;
            blurTolerance *= blurTolerance;
            var upsampleTolerance = Mathf.Pow(10, _upsampleTolerance);
            var noiseFilterWeight = 1 / (Mathf.Pow(10, _noiseFilterTolerance) + upsampleTolerance);

            _renderCommand.SetComputeVectorParam(_upsampleCompute, "InvLowResolution", lowResDepth.inverseDimensions);
            _renderCommand.SetComputeVectorParam(_upsampleCompute, "InvHighResolution", highResDepth.inverseDimensions);
            _renderCommand.SetComputeFloatParam(_upsampleCompute, "NoiseFilterStrength", noiseFilterWeight);
            _renderCommand.SetComputeFloatParam(_upsampleCompute, "StepSize", stepSize);
            _renderCommand.SetComputeFloatParam(_upsampleCompute, "kBlurTolerance", blurTolerance);
            _renderCommand.SetComputeFloatParam(_upsampleCompute, "kUpsampleTolerance", upsampleTolerance);

            _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "LoResDB", lowResDepth.id);
            _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "HiResDB", highResDepth.id);
            _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "LoResAO1", interleavedAO.id);

            if (highResAO != null)
                _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "HiResAO", highResAO.id);

            _renderCommand.SetComputeTextureParam(_upsampleCompute, kernel, "AoResult", dest.id);

            var xcount = (highResDepth.width  + 17) / 16;
            var ycount = (highResDepth.height + 17) / 16;
            _renderCommand.DispatchCompute(_upsampleCompute, kernel, xcount, ycount, 1);
        }

        void AddDebugCommands(CommandBuffer cmd)
        {
            _debugCommand.SetGlobalTexture("_AOTexture", _result.id);
            _debugCommand.Blit(null, BuiltinRenderTextureType.CurrentActive, _utilMaterial, 1);
        }

        #endregion
    }
}
