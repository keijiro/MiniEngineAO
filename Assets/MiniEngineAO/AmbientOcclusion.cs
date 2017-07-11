// MiniEngine SSAO for Unity
// https://github.com/keijiro/MiniEngineAO

using UnityEngine;
using UnityEngine.Rendering;

namespace MiniEngineAO
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(Camera))]
    public sealed class AmbientOcclusion : MonoBehaviour
    {
        #region Exposed properties

        // These properties are simply exposed from the original MiniEngine
        // AO shader. Some of them are not useful nor intuitive enough.
        // TODO: Needs UI/UX rework.

        [SerializeField, Range(-8, 0)] float _noiseFilterTolerance = -3;

        public float noiseFilterTolerance
        {
            get { return _noiseFilterTolerance; }
            set { _noiseFilterTolerance = value; }
        }

        [SerializeField, Range(-8, -1)] float _blurTolerance = -5;

        public float blurTolerance
        {
            get { return _blurTolerance; }
            set { _blurTolerance = value; }
        }

        [SerializeField, Range(-12, -1)] float _upsampleTolerance = -7;

        public float upsampleTolerance
        {
            get { return _upsampleTolerance; }
            set { _upsampleTolerance = value; }
        }

        [SerializeField, Range(1, 10)] float _rejectionFalloff = 2.5f;

        public float rejectionFalloff
        {
            get { return _rejectionFalloff; }
            set { _rejectionFalloff = value; }
        }

        [SerializeField, Range(0, 1)] float _accentuation = 0.1f;

        public float accentuation
        {
            get { return _accentuation; }
            set { _accentuation = value; }
        }

        #endregion

        #region Built-in resources

        [SerializeField, HideInInspector] ComputeShader _downsample1Compute;
        [SerializeField, HideInInspector] ComputeShader _downsample2Compute;
        [SerializeField, HideInInspector] ComputeShader _renderCompute;
        [SerializeField, HideInInspector] ComputeShader _upsampleCompute;
        [SerializeField, HideInInspector] Shader _utilShader;

        #endregion

        #region Detecting property changes

        float _noiseFilterToleranceOld;
        float _blurToleranceOld;
        float _upsampleToleranceOld;
        float _rejectionFalloffOld;
        float _accentuationOld;

        bool CheckUpdate(ref float oldValue, float current)
        {
            if (oldValue != current)
            {
                oldValue = current;
                return true;
            }
            else
            {
                return false;
            }
        }

        bool CheckPropertiesChanged()
        {
            return
                CheckUpdate(ref _noiseFilterToleranceOld, _noiseFilterTolerance) ||
                CheckUpdate(ref _blurToleranceOld,        _blurTolerance       ) ||
                CheckUpdate(ref _upsampleToleranceOld,    _upsampleTolerance   ) ||
                CheckUpdate(ref _rejectionFalloffOld,     _rejectionFalloff    ) ||
                CheckUpdate(ref _accentuationOld,         _accentuation        );
        }

        #endregion

        #region Render texture handle class

        // Render Texture Handle (RTHandle) is a class for handling render
        // textures that are internally used in AO rendering. It provides a
        // transparent interface for both statically allocated RTs and
        // temporary RTs allocated from command buffers.

        internal enum MipLevel { Original, L1, L2, L3, L4, L5, L6 }

        internal enum TextureType
        {
            Fixed, Half, Float,                        // 2D render texture
            FixedUAV, HalfUAV, FloatUAV,               // Read/write enabled
            FixedTiledUAV, HalfTiledUAV, FloatTiledUAV // Texture array
        }

        internal class RTHandle
        {
            // Base dimensions (shared between handles)
            static int _baseWidth;
            static int _baseHeight;

            public static void SetBaseDimensions(int w, int h)
            {
                _baseWidth = w;
                _baseHeight = h;
            }

            public static bool CheckBaseDimensions(int w, int h)
            {
                return _baseWidth == w && _baseHeight == h;
            }

            // Public properties
            public int nameID { get { return _id; } }
            public int width { get { return _width; } }
            public int height { get { return _height; } }
            public int depth { get { return isTiled ? 16 : 1; } }
            public bool isTiled { get { return (int)_type > 5; } }
            public bool hasUAV { get { return (int)_type > 2; } }

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

            public Vector2 inverseDimensions
            {
                get { return new Vector2(1.0f / width, 1.0f / height); }
            }

            // Constructor
            public RTHandle(string name, TextureType type, MipLevel level)
            {
                _id = Shader.PropertyToID(name);
                _type = type;
                _level = level;
            }

            // Allocate the buffer in advance of use.
            public void AllocateNow()
            {
                CalculateDimensions();

                if (_rt == null)
                {
                    // Initial allocation.
                    _rt = new RenderTexture(
                        _width, _height, 0,
                        renderTextureFormat,
                        RenderTextureReadWrite.Linear
                    );
                    _rt.hideFlags = HideFlags.DontSave;
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
                    _rt.volumeDepth = depth;
                }

                _rt.Create();
            }

            // Push the allocation command to the given command buffer.
            public void PushAllocationCommand(CommandBuffer cmd)
            {
                CalculateDimensions();

                cmd.GetTemporaryRT(
                    _id, _width, _height, 0,
                    FilterMode.Point, renderTextureFormat,
                    RenderTextureReadWrite.Linear, 1, hasUAV
                );
            }

            // Destroy internal objects.
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
            TextureType _type;
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

            // Calculate width/height of the texture from the base dimensions.
            void CalculateDimensions()
            {
                var div = 1 << (int)_level;
                _width  = (_baseWidth  + (div - 1)) / div;
                _height = (_baseHeight + (div - 1)) / div;
            }
        }

        #endregion

        #region Internal objects

        Camera _camera;

        RTHandle _depthCopy;
        RTHandle _linearDepth;
        RTHandle _lowDepth1;
        RTHandle _lowDepth2;
        RTHandle _lowDepth3;
        RTHandle _lowDepth4;
        RTHandle _tiledDepth1;
        RTHandle _tiledDepth2;
        RTHandle _tiledDepth3;
        RTHandle _tiledDepth4;
        RTHandle _occlusion1;
        RTHandle _occlusion2;
        RTHandle _occlusion3;
        RTHandle _occlusion4;
        RTHandle _composite1;
        RTHandle _composite2;
        RTHandle _composite3;
        RTHandle _result;

        CommandBuffer _renderCommand;
        CommandBuffer _debugCommand;

        Material _utilMaterial;

        #endregion

        #region MonoBehaviour functions

        void OnEnable()
        {
            if (_renderCommand != null) RegisterCommandBuffers();
        }

        void OnDisable()
        {
            if (_renderCommand != null) UnregisterCommandBuffers();
        }

        void LateUpdate()
        {
            DoLazyInitialization();

            // Check if we have to rebuild the command buffers.
            var rebuild = CheckPropertiesChanged();

            // Check if the screen size was changed from the previous frame.
            // We have to rebuild the command buffer if it's changed.
            rebuild |= !RTHandle.CheckBaseDimensions(_camera.pixelWidth, _camera.pixelHeight);

            // In edit mode, it's difficult to check all the elements that
            // affect the AO, so we update the command buffer every time.
            rebuild |= !Application.isPlaying;

            if (rebuild) RebuildCommandBuffers();
        }

        void OnDestroy()
        {
            if (_result != null)
            {
                _tiledDepth1.Destroy();
                _tiledDepth2.Destroy();
                _tiledDepth3.Destroy();
                _tiledDepth4.Destroy();
                _result.Destroy();
            }

            if (_renderCommand != null)
            {
                _renderCommand.Dispose();
                _debugCommand.Dispose();
            }

            if (_utilMaterial != null)
            {
                if (Application.isPlaying)
                    Destroy(_utilMaterial);
                else
                    DestroyImmediate(_utilMaterial);
            }
        }

        #endregion

        #region Private methods

        void RegisterCommandBuffers()
        {
            _camera.AddCommandBuffer(CameraEvent.BeforeLighting, _renderCommand);
            _camera.AddCommandBuffer(CameraEvent.BeforeLighting, _debugCommand);
        }

        void UnregisterCommandBuffers()
        {
            _camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, _renderCommand);
            _camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, _debugCommand);
        }

        void DoLazyInitialization()
        {
            // Camera reference
            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
                // We requires the camera depth texture.
                _camera.depthTextureMode = DepthTextureMode.Depth;
            }

            // Render texture handles
            if (_result == null)
            {
                _depthCopy = new RTHandle("DepthCopy", TextureType.Float, MipLevel.Original);
                _linearDepth = new RTHandle("LinearDepth", TextureType.HalfUAV, MipLevel.Original);

                _lowDepth1 = new RTHandle("LowDepth1", TextureType.FloatUAV, MipLevel.L1);
                _lowDepth2 = new RTHandle("LowDepth2", TextureType.FloatUAV, MipLevel.L2);
                _lowDepth3 = new RTHandle("LowDepth3", TextureType.FloatUAV, MipLevel.L3);
                _lowDepth4 = new RTHandle("LowDepth4", TextureType.FloatUAV, MipLevel.L4);

                _tiledDepth1 = new RTHandle("TiledDepth1", TextureType.HalfTiledUAV, MipLevel.L3);
                _tiledDepth2 = new RTHandle("TiledDepth2", TextureType.HalfTiledUAV, MipLevel.L4);
                _tiledDepth3 = new RTHandle("TiledDepth3", TextureType.HalfTiledUAV, MipLevel.L5);
                _tiledDepth4 = new RTHandle("TiledDepth4", TextureType.HalfTiledUAV, MipLevel.L6);

                _occlusion1 = new RTHandle("Occlusion1", TextureType.FixedUAV, MipLevel.L1);
                _occlusion2 = new RTHandle("Occlusion2", TextureType.FixedUAV, MipLevel.L2);
                _occlusion3 = new RTHandle("Occlusion3", TextureType.FixedUAV, MipLevel.L3);
                _occlusion4 = new RTHandle("Occlusion4", TextureType.FixedUAV, MipLevel.L4);

                _composite1 = new RTHandle("Composite1", TextureType.FixedUAV, MipLevel.L1);
                _composite2 = new RTHandle("Composite2", TextureType.FixedUAV, MipLevel.L2);
                _composite3 = new RTHandle("Composite3", TextureType.FixedUAV, MipLevel.L3);

                _result = new RTHandle("AmbientOcclusion", TextureType.FixedUAV, MipLevel.Original);
            }

            // Command buffers
            if (_renderCommand == null)
            {
                _renderCommand = new CommandBuffer();
                _renderCommand.name = "SSAO";

                _debugCommand = new CommandBuffer();
                _debugCommand.name = "SSAO Debug";
            }

            // Materials
            if (_utilMaterial == null)
            {
                _utilMaterial = new Material(_utilShader);
                _utilMaterial.hideFlags = HideFlags.DontSave;
            }
        }

        void RebuildCommandBuffers()
        {
            UnregisterCommandBuffers();

            // Update the base dimensions and reallocate static RTs.
            RTHandle.SetBaseDimensions(_camera.pixelWidth, _camera.pixelHeight);

            _tiledDepth1.AllocateNow();
            _tiledDepth2.AllocateNow();
            _tiledDepth3.AllocateNow();
            _tiledDepth4.AllocateNow();

            _result.AllocateNow();

            // Rebuild the render commands.
            _renderCommand.Clear();

            PushDownsampleCommands(_renderCommand);

            _occlusion1.PushAllocationCommand(_renderCommand);
            _occlusion2.PushAllocationCommand(_renderCommand);
            _occlusion3.PushAllocationCommand(_renderCommand);
            _occlusion4.PushAllocationCommand(_renderCommand);

            var tanHalfFovH = CalculateTanHalfFovHeight();
            PushRenderCommands(_renderCommand, _tiledDepth1, _occlusion1, tanHalfFovH);
            PushRenderCommands(_renderCommand, _tiledDepth2, _occlusion2, tanHalfFovH);
            PushRenderCommands(_renderCommand, _tiledDepth3, _occlusion3, tanHalfFovH);
            PushRenderCommands(_renderCommand, _tiledDepth4, _occlusion4, tanHalfFovH);

            _composite1.PushAllocationCommand(_renderCommand);
            _composite2.PushAllocationCommand(_renderCommand);
            _composite3.PushAllocationCommand(_renderCommand);

            PushUpsampleCommands(_renderCommand, _lowDepth4, _occlusion4, _lowDepth3, _occlusion3, _composite3);
            PushUpsampleCommands(_renderCommand, _lowDepth3, _composite3, _lowDepth2, _occlusion2, _composite2);
            PushUpsampleCommands(_renderCommand, _lowDepth2, _composite2, _lowDepth1, _occlusion1, _composite1);
            PushUpsampleCommands(_renderCommand, _lowDepth1, _composite1, _linearDepth, null, _result);

            // Rebuild the debug commands.
            _debugCommand.Clear();
            PushDebugCommands(_debugCommand);

            RegisterCommandBuffers();
        }

        #endregion

        #region Utilities for command buffer builders

        bool CheckIfResolvedDepthAvailable()
        {
            // AFAIK, resolved depth is only available on D3D11/12.
            // TODO: Is there more proper way to determine this?
            var rpath = _camera.actualRenderingPath;
            var gtype = SystemInfo.graphicsDeviceType;
            return rpath == RenderingPath.DeferredShading &&
                  (gtype == GraphicsDeviceType.Direct3D11 ||
                   gtype == GraphicsDeviceType.Direct3D12);
        }

        // Calculate values in _ZBuferParams (built-in shader variable)
        // We can't use _ZBufferParams in compute shaders, so this function is
        // used to give the values in it to compute shaders.
        Vector4 CalculateZBufferParams()
        {
            var fpn = _camera.farClipPlane / _camera.nearClipPlane;
            if (SystemInfo.usesReversedZBuffer)
                return new Vector4(fpn - 1, 1, 0, 0);
            else
                return new Vector4(1 - fpn, fpn, 0, 0);
        }

        float CalculateTanHalfFovHeight()
        {
            return 1 / _camera.projectionMatrix[0, 0];
        }

        // The arrays below are reused between frames to reduce GC allocation.

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

        void PushDownsampleCommands(CommandBuffer cmd)
        {
            // Make a copy of the depth texture, or reuse the resolved depth
            // buffer (it's only available in some specific situations).
            var useDepthCopy = !CheckIfResolvedDepthAvailable();
            if (useDepthCopy)
            {
                _depthCopy.PushAllocationCommand(cmd);
                cmd.Blit(null, _depthCopy.id, _utilMaterial, 0);
            }

            // Temporary buffer allocations.
            _linearDepth.PushAllocationCommand(cmd);
            _lowDepth1.PushAllocationCommand(cmd);
            _lowDepth2.PushAllocationCommand(cmd);
            _lowDepth3.PushAllocationCommand(cmd);
            _lowDepth4.PushAllocationCommand(cmd);

            // 1st downsampling pass.
            var cs = _downsample1Compute;
            var kernel = cs.FindKernel("main");

            cmd.SetComputeTextureParam(cs, kernel, "LinearZ", _linearDepth.id);
            cmd.SetComputeTextureParam(cs, kernel, "DS2x", _lowDepth1.id);
            cmd.SetComputeTextureParam(cs, kernel, "DS4x", _lowDepth2.id);
            cmd.SetComputeTextureParam(cs, kernel, "DS2xAtlas", _tiledDepth1.id);
            cmd.SetComputeTextureParam(cs, kernel, "DS4xAtlas", _tiledDepth2.id);
            cmd.SetComputeVectorParam(cs, "ZBufferParams", CalculateZBufferParams());

            if (useDepthCopy)
                cmd.SetComputeTextureParam(cs, kernel, "Depth", _depthCopy.id);
            else
                cmd.SetComputeTextureParam(cs, kernel, "Depth", BuiltinRenderTextureType.ResolvedDepth);

            cmd.DispatchCompute(cs, kernel, _tiledDepth2.width, _tiledDepth2.height, 1);

            if (useDepthCopy) cmd.ReleaseTemporaryRT(_depthCopy.nameID);

            // 2nd downsampling pass.
            cs = _downsample2Compute;
            kernel = cs.FindKernel("main");

            cmd.SetComputeTextureParam(cs, kernel, "DS4x", _lowDepth2.id);
            cmd.SetComputeTextureParam(cs, kernel, "DS8x", _lowDepth3.id);
            cmd.SetComputeTextureParam(cs, kernel, "DS16x", _lowDepth4.id);
            cmd.SetComputeTextureParam(cs, kernel, "DS8xAtlas", _tiledDepth3.id);
            cmd.SetComputeTextureParam(cs, kernel, "DS16xAtlas", _tiledDepth4.id);

            cmd.DispatchCompute(cs, kernel, _tiledDepth4.width, _tiledDepth4.height, 1);
        }

        void PushRenderCommands(CommandBuffer cmd, RTHandle source, RTHandle dest, float TanHalfFovH)
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
            var cs = _renderCompute;
            var kernel = cs.FindKernel("main_interleaved");

            cmd.SetComputeFloatParams(cs, "gInvThicknessTable", InvThicknessTable);
            cmd.SetComputeFloatParams(cs, "gSampleWeightTable", SampleWeightTable);
            cmd.SetComputeVectorParam(cs, "gInvSliceDimension", source.inverseDimensions);
            cmd.SetComputeFloatParam(cs, "gRejectFadeoff", -1 / _rejectionFalloff);
            cmd.SetComputeFloatParam(cs, "gRcpAccentuation", 1 / (1 + _accentuation));
            cmd.SetComputeTextureParam(cs, kernel, "DepthTex", source.id);
            cmd.SetComputeTextureParam(cs, kernel, "Occlusion", dest.id);

            // Calculate the thread group count and add a dispatch command with them.
            uint xsize, ysize, zsize;
            cs.GetKernelThreadGroupSizes(kernel, out xsize, out ysize, out zsize);

            cmd.DispatchCompute(
                cs, kernel,
                (source.width  + (int)xsize - 1) / (int)xsize,
                (source.height + (int)ysize - 1) / (int)ysize,
                (source.depth  + (int)zsize - 1) / (int)zsize
            );
        }

        void PushUpsampleCommands(
            CommandBuffer cmd,
            RTHandle lowResDepth, RTHandle interleavedAO,
            RTHandle highResDepth, RTHandle highResAO,
            RTHandle dest
        )
        {
            var cs = _upsampleCompute;
            var kernel = cs.FindKernel((highResAO == null) ? "main" : "main_blendout");

            var stepSize = 1920.0f / lowResDepth.width;
            var blurTolerance = 1 - Mathf.Pow(10, _blurTolerance) * stepSize;
            blurTolerance *= blurTolerance;
            var upsampleTolerance = Mathf.Pow(10, _upsampleTolerance);
            var noiseFilterWeight = 1 / (Mathf.Pow(10, _noiseFilterTolerance) + upsampleTolerance);

            cmd.SetComputeVectorParam(cs, "InvLowResolution", lowResDepth.inverseDimensions);
            cmd.SetComputeVectorParam(cs, "InvHighResolution", highResDepth.inverseDimensions);
            cmd.SetComputeFloatParam(cs, "NoiseFilterStrength", noiseFilterWeight);
            cmd.SetComputeFloatParam(cs, "StepSize", stepSize);
            cmd.SetComputeFloatParam(cs, "kBlurTolerance", blurTolerance);
            cmd.SetComputeFloatParam(cs, "kUpsampleTolerance", upsampleTolerance);

            cmd.SetComputeTextureParam(cs, kernel, "LoResDB", lowResDepth.id);
            cmd.SetComputeTextureParam(cs, kernel, "HiResDB", highResDepth.id);
            cmd.SetComputeTextureParam(cs, kernel, "LoResAO1", interleavedAO.id);

            if (highResAO != null)
                cmd.SetComputeTextureParam(cs, kernel, "HiResAO", highResAO.id);

            cmd.SetComputeTextureParam(cs, kernel, "AoResult", dest.id);

            var xcount = (highResDepth.width  + 17) / 16;
            var ycount = (highResDepth.height + 17) / 16;
            cmd.DispatchCompute(cs, kernel, xcount, ycount, 1);
        }

        void PushDebugCommands(CommandBuffer cmd)
        {
            cmd.SetGlobalTexture("_AOTexture", _result.id);
            cmd.Blit(null, BuiltinRenderTextureType.CurrentActive, _utilMaterial, 1);
        }

        #endregion
    }
}
