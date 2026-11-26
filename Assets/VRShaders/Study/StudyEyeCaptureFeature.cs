using System;
using System.IO;
using Thesis.Glass.Reference;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Thesis.Glass.Study
{
    public sealed class StudyEyeCaptureFeature : ScriptableRendererFeature
    {
        [Serializable] public sealed class CaptureRecord
        {
            public int schema_version = 1, frame, block, condition_index, sample_index;
            public float phase, realtime;
            public string material, path, scene_package;
            public string source_graphics_format;
            public string copy_method = "compute explicit texture layers v3";
            public int[] eye_slices;
            public int source_slices, source_msaa;
            public float render_scale;
            public bool baseline, rows_flipped;
            public string capture_stage = "linear scene colour after transparents, before post-processing; not compositor output";
            public ReferenceSceneExporter.CameraRecord[] cameras;
            public string[] images;
        }
        public sealed class Request
        {
            public Camera camera;
            public string directory;
            public CaptureRecord record;
            public bool queued, completed, cancelled;
            public string error;
        }
        public static Request Pending { get; private set; }
        public static Request Schedule(Camera camera, string directory, CaptureRecord record)
        {
            if (Pending != null) throw new InvalidOperationException("An eye capture is already pending.");
            if (!SystemInfo.supportsAsyncGPUReadback) throw new NotSupportedException("Async GPU readback is unavailable.");
            Pending = new Request { camera = camera, directory = directory, record = record };
            return Pending;
        }
        public static void Cancel(Request request)
        {
            if (request == null) return;
            request.cancelled = true;
            if (Pending == request) Pending = null;
        }
        static void Fail(Request request, string error)
        {
            request.error = error; request.completed = true;
            if (Pending == request) Pending = null;
        }
        CapturePass pass;
        public override void Create() => pass = new CapturePass();
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (Pending != null && !Pending.queued && Pending.camera == renderingData.cameraData.camera)
                renderer.EnqueuePass(pass);
        }

        sealed class ReadbackData
        {
            public TextureHandle handle;
            public RTHandle owned;
            public Request request;
            public int width, height;
            public int[] slices;
            public RectInt[] crops;
        }
        sealed class CopyData
        {
            public TextureHandle source, destination;
            public ComputeShader shader;
            public int kernel, width, height, slices;
            public bool array;
        }
        sealed class CapturePass : ScriptableRenderPass
        {
            public CapturePass()
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                requiresIntermediateTexture = true;
            }
            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                Request request = Pending;
                UniversalCameraData camera = frameData.Get<UniversalCameraData>();
                if (request == null || request.queued || request.cancelled || camera.camera != request.camera) return;
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer) { Fail(request, "Capture requires a readable intermediate colour target."); return; }
                if (camera.postProcessEnabled) { Fail(request, "Disable post-processing for linear reference capture."); return; }
                if (!camera.isDefaultViewport) { Fail(request, "Custom camera rectangles are not supported by study capture."); return; }
                if (camera.xr.enabled && !camera.xr.singlePassEnabled) { Fail(request, "Image capture currently requires single-pass stereo, not XR multipass."); return; }
                TextureHandle source = resources.activeColorTexture;
                TextureDesc desc = source.GetDescriptor(graph);
                if (desc.dimension != TextureDimension.Tex2D && desc.dimension != TextureDimension.Tex2DArray)
                { Fail(request, "Unsupported eye texture dimension."); return; }
                int views = camera.xr.enabled ? camera.xr.viewCount : 1;
                int[] slices = new int[views];
                request.record.eye_slices = slices;
                RectInt[] crops = new RectInt[views];
                request.record.cameras = new ReferenceSceneExporter.CameraRecord[views];
                request.record.images = new string[views];
                request.record.frame = Time.frameCount;
                request.record.realtime = Time.realtimeSinceStartup;
                request.record.source_graphics_format = desc.colorFormat.ToString();
                request.record.source_slices = desc.slices;
                request.record.source_msaa = (int)desc.msaaSamples;
                request.record.render_scale = camera.renderScale;
                for (int eye = 0; eye < views; eye++)
                {
                    string name = views == 1 ? "mono" : eye == 0 ? "left" : "right";
                    slices[eye] = camera.xr.enabled ? camera.xr.GetTextureArraySlice(eye) : 0;
                    Rect viewport = camera.xr.enabled ? camera.xr.GetViewport(eye) : new Rect(0, 0, desc.width, desc.height);
                    float nativeW = camera.xr.enabled ? camera.xr.renderTargetDesc.width : desc.width;
                    float nativeH = camera.xr.enabled ? camera.xr.renderTargetDesc.height : desc.height;
                    crops[eye] = new RectInt(Mathf.RoundToInt(viewport.x / nativeW * desc.width), Mathf.RoundToInt(viewport.y / nativeH * desc.height),
                        Mathf.RoundToInt(viewport.width / nativeW * desc.width), Mathf.RoundToInt(viewport.height / nativeH * desc.height));
                    RectInt crop = crops[eye];
                    if (crop.width < 1 || crop.height < 1 || crop.x < 0 || crop.y < 0 || crop.xMax > desc.width || crop.yMax > desc.height || slices[eye] < 0 || slices[eye] >= desc.slices)
                    { Fail(request, "Eye viewport/slice is outside the captured texture."); return; }
                    request.record.cameras[eye] = new ReferenceSceneExporter.CameraRecord { name = camera.camera.name, eye = name,
                        view = Matrix(camera.GetViewMatrix(eye)), projection = Matrix(camera.GetProjectionMatrix(eye)),
                        width = crop.width, height = crop.height, near = camera.camera.nearClipPlane, far = camera.camera.farClipPlane,
                        orthographic = camera.camera.orthographic, culling_mask = camera.camera.cullingMask };
                    request.record.images[eye] = $"frame_{request.record.sample_index:D4}_{name}.exr";
                }
                if (views == 2 && desc.dimension == TextureDimension.Tex2DArray && slices[0] == slices[1])
                { Fail(request, "XR reported the same array slice for both eyes; refusing duplicate stereo output."); return; }
                request.queued = true;
                var descriptor = new RenderTextureDescriptor(desc.width, desc.height)
                {
                    graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat, depthStencilFormat = GraphicsFormat.None,
                    dimension = desc.dimension, volumeDepth = desc.slices, msaaSamples = 1,
                    useDynamicScale = false, useMipMap = false, enableRandomWrite = true
                };
                var texture = new RenderTexture(descriptor) { name = "Study linear eye snapshot" };
                texture.Create();
                RTHandle owned = RTHandles.Alloc(texture, transferOwnership: true);
                TextureHandle destination = graph.ImportTexture(owned);
                ComputeShader copy = Resources.Load<ComputeShader>("StudyEyeCopy");
                if (copy == null || !SystemInfo.supportsComputeShaders)
                {
                    owned.Release(); Fail(request, "StudyEyeCopy compute resource/support unavailable."); return;
                }
                using (var builder = graph.AddComputePass<CopyData>("Study explicit layer snapshot", out var data))
                {
                    data.source = source; data.destination = destination; data.shader = copy;
                    data.array = desc.dimension == TextureDimension.Tex2DArray;
                    data.kernel = copy.FindKernel(data.array ? "CopyArray" : "Copy2D");
                    data.width = desc.width; data.height = desc.height; data.slices = desc.slices;
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.UseTexture(destination, AccessFlags.Write);
                    builder.SetRenderFunc(static (CopyData d, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeIntParam(d.shader, "_Width", d.width);
                        context.cmd.SetComputeIntParam(d.shader, "_Height", d.height);
                        context.cmd.SetComputeIntParam(d.shader, "_Slices", d.slices);
                        context.cmd.SetComputeTextureParam(d.shader, d.kernel, d.array ? "_SourceArray" : "_Source2D", d.source);
                        context.cmd.SetComputeTextureParam(d.shader, d.kernel, d.array ? "_DestinationArray" : "_Destination2D", d.destination);
                        context.cmd.DispatchCompute(d.shader, d.kernel, (d.width + 7) / 8, (d.height + 7) / 8, d.array ? d.slices : 1);
                    });
                }
                using (var builder = graph.AddUnsafePass<ReadbackData>("Study async readback", out var data))
                {
                    data.handle = destination; data.owned = owned; data.request = request;
                    data.width = desc.width; data.height = desc.height; data.slices = slices; data.crops = crops;
                    builder.UseTexture(destination, AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (ReadbackData d, UnsafeGraphContext context) =>
                    {
                        CommandBuffer command = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        command.RequestAsyncReadback(d.owned.rt, 0, TextureFormat.RGBAHalf, result => Save(d, result));
                    });
                }
            }
        }
        static void Save(ReadbackData data, AsyncGPUReadbackRequest result)
        {
            Request request = data.request;
            try
            {
                if (request.cancelled) return;
                if (result.hasError) throw new IOException("GPU eye readback failed.");
                Directory.CreateDirectory(request.directory);
                for (int eye = 0; eye < data.slices.Length; eye++)
                {
                    NativeArray<byte> source = result.GetData<byte>(data.slices[eye]);
                    RectInt crop = data.crops[eye];
                    int rowBytes = crop.width * 8;
                    using (var pixels = new NativeArray<byte>(crop.height * rowBytes, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                    {
                        for (int y = 0; y < crop.height; y++)
                        {
                            int sy = request.record.rows_flipped ? data.height - 1 - (crop.y + y) : crop.y + y;
                            NativeArray<byte>.Copy(source, (sy * data.width + crop.x) * 8, pixels, y * rowBytes, rowBytes);
                        }
                        using (NativeArray<byte> encoded = ImageConversion.EncodeNativeArrayToEXR(pixels, GraphicsFormat.R16G16B16A16_SFloat,
                            (uint)crop.width, (uint)crop.height, 0, Texture2D.EXRFlags.CompressZIP))
                            File.WriteAllBytes(Path.Combine(request.directory, request.record.images[eye]), encoded.ToArray());
                    }
                }
                File.WriteAllText(Path.Combine(request.directory, $"frame_{request.record.sample_index:D4}.json"), JsonUtility.ToJson(request.record, true));
                request.completed = true;
            }
            catch (Exception e) { Fail(request, e.Message); }
            finally
            {
                data.owned.Release();
                if (Pending == request) Pending = null;
            }
        }
        static float[] Matrix(Matrix4x4 value)
        {
            var result = new float[16];
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) result[r * 4 + c] = value[r, c];
            return result;
        }
    }
}
