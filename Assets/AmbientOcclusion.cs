using UnityEngine;
using UnityEngine.Rendering;

namespace ComputeAO
{
    [RequireComponent(typeof(Camera))]
    public class AmbientOcclusion : MonoBehaviour
    {
        #region Exposed attributes

        [SerializeField, Range(1, 10)] float _rejectionFalloff = 2.5f;
        [SerializeField, Range(0, 1)] float _accentuation = 0.1f;
        [SerializeField, Range(1, 4)] int _hierarchyDepth = 3;

        #endregion

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

        void UpdateComputeParameters(float width, float height, float fov)
        {
            var TanHalfFovH = Mathf.Tan(fov / 2);

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
            float InverseRangeFactor = 1.0f / ThicknessMultiplier;

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

            // Normalize the weights by dividing by the sum of all weights
            var totalWeight = 0.0f;

            foreach (var w in SampleWeightTable)
                totalWeight += w;

            for (var i = 0; i < SampleWeightTable.Length; i++)
                SampleWeightTable[i] /= totalWeight;

            _aoCommand.SetComputeFloatParams(_compute, "InvThicknessTable", InvThicknessTable);
            _aoCommand.SetComputeFloatParams(_compute, "SampleWeightTable", SampleWeightTable);
            _aoCommand.SetComputeFloatParam(_compute, "InvSliceWidth", 1 / width);
            _aoCommand.SetComputeFloatParam(_compute, "InvSliceHeight", 1 / height);
            _aoCommand.SetComputeFloatParam(_compute, "RejectFadeoff", 1 / -_rejectionFalloff);
            _aoCommand.SetComputeFloatParam(_compute, "RcpAccentuation", 1 / (1 + _accentuation));
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

            UpdateComputeParameters(camera.pixelWidth, camera.pixelHeight, camera.fieldOfView);

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
