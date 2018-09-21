﻿// ReSharper disable RedundantUsingDirective

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using AcTools;
using AcTools.DataFile;
using AcTools.Kn5File;
using AcTools.Render.Base;
using AcTools.Render.Base.Cameras;
using AcTools.Render.Base.Materials;
using AcTools.Render.Base.Objects;
using AcTools.Render.Base.PostEffects;
using AcTools.Render.Base.Structs;
using AcTools.Render.Base.TargetTextures;
using AcTools.Render.Base.Utils;
using AcTools.Render.Kn5Specific.Materials;
using AcTools.Render.Kn5Specific.Objects;
using AcTools.Render.Kn5Specific.Textures;
using AcTools.Render.Kn5SpecificSpecial;
using AcTools.Render.Shaders;
using AcTools.Render.Wrapper;
using AcTools.Utils;
using AcTools.Utils.Helpers;
using CustomTracksBakery.Shaders;
using JetBrains.Annotations;
using Newtonsoft.Json;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDX.DXGI;
using StringBasedFilter;
using StringBasedFilter.Parsing;
using StringBasedFilter.TestEntries;
using Device = SlimDX.Direct3D11.Device;
using Filter = StringBasedFilter.Filter;
using Half = SystemHalf.Half;
using Resource = SlimDX.Direct3D11.Resource;

namespace CustomTracksBakery {
    public enum BakedMode {
        Tangent = 0,
        TangentLength = 1
    }

    public enum PatchEntryType {
        Tangent = 0,
        TangentLength = 1,
        Normal = 2,
        NormalLength = 3
    }

    public class BakedObjectPiece : TrianglesRenderableObject<InputLayouts.VerticePNTG> {
        public new readonly BoundingBox BoundingBox;
        public readonly Vector3 BbCenter;
        public readonly Vector3 BbSize;
        public readonly float BbSizeLength;

        public float GetHorizontalDistance(Vector3 v) {
            var p = BbCenter;
            var s = BbSize;

            var x = v.X - p.X;
            if (x < 0) x = -x;
            x -= s.X / 2f;

            var y = v.Y - p.Y;
            if (y < 0) y = -y;
            y -= s.Y / 2f;

            var r = x < y ? x : y;
            return r > 0 ? r : 0;
        }

        public BakedObjectPiece(Kn5Node node) : base(node.Name, InputLayouts.VerticePNTG.Convert(node.Vertices), node.Indices.ToIndicesFixX()) {
            var boundingBoxStart = node.Vertices.Length > 0 ? node.Vertices[0].Position.ToVector3() : Vector3.Zero;
            var boundingBox = new BoundingBox(boundingBoxStart, boundingBoxStart);
            for (var i = node.Vertices.Length - 1; i > 0; i--) {
                node.Vertices[i].Position.ExtendBoundingBox(ref boundingBox);
            }

            BoundingBox = boundingBox;
            BbCenter = boundingBox.GetCenter();
            BbSize = boundingBox.GetSize();
            BbSizeLength = boundingBox.GetSize().Length();
        }
    }

    public sealed class BakedObject : IRenderableObject {
        public readonly Kn5 ObjectKn5;
        public readonly BakedMode Mode;
        public readonly bool IsTree, IsGrass, IsToSyncNormals, IsSurface;

        public Kn5Node OriginalNode { get; }
        public readonly BoundingBox BoundingBox;
        public readonly Vector3 BbCenter;
        public readonly Vector3 BbSize;
        public readonly float BbSizeLength;

        private static readonly int[] SplitAxis = { 0, 2 };

        private static void LoadRenderables(IList<BakedObjectPiece> destination, Kn5Node node, int step, float splitDistance) {
            if (step >= SplitAxis.Length) {
                destination.Add(new BakedObjectPiece(node));
                return;
            }

            var splitIndex = SplitAxis[step];
            var min = float.MaxValue;
            var max = float.MinValue;
            foreach (var vertex in node.Vertices) {
                var pos = vertex.Position[splitIndex];
                if (pos < min) min = pos;
                if (pos > max) max = pos;
            }

            if (min > max) {
                return;
            }

            var axisSize = max - min;
            var fromBase = min;

            if (axisSize < splitDistance * 1.2f) {
                LoadRenderables(destination, node, step + 1, splitDistance);
                return;
            }

            var pieces = (int)Math.Ceiling(axisSize / splitDistance);
            var origInd = node.Indices;
            var origVer = node.Vertices;
            var vertices = new List<Kn5Node.Vertex>();
            var indices = new List<ushort>();

            for (var i = 0; i < pieces; i++) {
                var from = fromBase + (float)i / pieces * axisSize;
                var to = fromBase + (i + 1f) / pieces * axisSize + (i == pieces - 1 ? 1f : 0f);

                for (var j = 0; j < origInd.Length - 2; j += 3) {
                    var v0 = origVer[origInd[j]];
                    var v1 = origVer[origInd[j + 1]];
                    var v2 = origVer[origInd[j + 2]];
                    var avg = (v0.Position[splitIndex] + v1.Position[splitIndex] + v2.Position[splitIndex]) / 3f;
                    if (avg >= @from && avg < to) {
                        indices.Add((ushort)vertices.Count);
                        indices.Add((ushort)(vertices.Count + 1));
                        indices.Add((ushort)(vertices.Count + 2));
                        vertices.Add(v0);
                        vertices.Add(v1);
                        vertices.Add(v2);
                    }
                }

                if (indices.Count > 0) {
                    var mesh = Kn5Node.CreateBaseNode(node.Name);
                    mesh.NodeClass = Kn5NodeClass.Mesh;
                    mesh.Vertices = vertices.ToArray();
                    mesh.Indices = indices.ToArray();
                    mesh.MaterialId = node.MaterialId;
                    mesh.LodIn = node.LodIn;
                    mesh.LodOut = node.LodOut;
                    mesh.Layer = node.Layer;
                    mesh.IsVisible = node.IsVisible;
                    mesh.IsRenderable = true;
                    mesh.CastShadows = true;
                    LoadRenderables(destination, mesh, step + 1, splitDistance);

                    vertices.Clear();
                    indices.Clear();
                }
            }
        }

        private BakedObjectPiece[] _renderables;

        public BakedObject(Kn5Node node, Kn5 kn5, [NotNull] BakedObjectFilters filters, float splitDistance) {
            if (splitDistance <= 1f) {
                _renderables = new[] {
                    new BakedObjectPiece(node)
                };
            } else {
                var list = new List<BakedObjectPiece>();
                LoadRenderables(list, node, 0, splitDistance);
                _renderables = list.ToArray();
            }

            var boundingBoxStart = node.Vertices.Length > 0 ? node.Vertices[0].Position.ToVector3() : Vector3.Zero;
            var boundingBox = new BoundingBox(boundingBoxStart, boundingBoxStart);
            for (var i = node.Vertices.Length - 1; i > 0; i--) {
                node.Vertices[i].Position.ExtendBoundingBox(ref boundingBox);
            }

            BoundingBox = boundingBox;
            BbCenter = boundingBox.GetCenter();
            BbSize = boundingBox.GetSize();
            BbSizeLength = boundingBox.GetSize().Length();
            // Trace.WriteLine("Mesh: " + node.Name + ", split into: " + _renderables.Length
            // + ", BB: at " + boundingBox.GetCenter() + ", size=" + boundingBox.GetSize());

            OriginalNode = node;
            ObjectKn5 = kn5;

            if (GetMaterial()?.GetMappingByName("txNormal") != null
                    || GetMaterial()?.GetMappingByName("txNormalDetail") != null
                    || GetMaterial()?.GetMappingByName("txDetailNM") != null) {
                Mode = BakedMode.TangentLength;
            } else {
                Mode = BakedMode.Tangent;
            }

            var isNotRegular = filters.RegularObjects?.Test(this) == false;
            IsTree = isNotRegular && filters.Tree?.Test(this) == true;
            IsGrass = isNotRegular && filters.Grass?.Test(this) == true;
            IsToSyncNormals = filters.SyncNormals?.Test(this) == true;
            IsSurface = filters.Surfaces?.Test(this) == true;
        }

        public float GetHorizontalDistance(Vector3 v) {
            var p = BbCenter;
            var s = BbSize;
            var x = Math.Abs(v.X - p.X) - s.X / 2f;
            var y = Math.Abs(v.Z - p.Z) - s.Z / 2f;
            return Math.Max(Math.Min(x, y), 0f);
        }

        [CanBeNull]
        public Kn5Material GetMaterial() {
            return ObjectKn5.GetMaterial(OriginalNode.MaterialId);
        }

        [CanBeNull]
        public string GetShaderName() {
            return GetMaterial()?.ShaderName;
        }

        [CanBeNull]
        public string GetMaterialName() {
            return GetMaterial()?.Name;
        }

        private Kn5MaterialToBake _material;

        public void Initialize(IDeviceContextHolder contextHolder) {
            foreach (var renderable in _renderables) {
                renderable.Draw(contextHolder, null, SpecialRenderMode.InitializeOnly);
            }

            if (_material == null) {
                _material = (Kn5MaterialToBake)contextHolder.Get<SharedMaterials>().GetMaterial(OriginalNode.MaterialId);
                _material.EnsureInitialized(contextHolder);
            }
        }

        private static BakedMode _setBakedMode;

        public void RenderNode(Vector3 pos, Vector3 look, DeviceContextHolder h, float distanceThreshold, EffectBakeryShaders effect) {
            if ((Kn5MaterialToBake.SecondPass || Kn5MaterialToBake.GrassPass) && (Mode != _setBakedMode)) {
                effect.FxSecondPassMode.Set((float)Mode);
                _setBakedMode = Mode;
            }

            var set = false;
            for (var i = _renderables.Length - 1; i >= 0; i--) {
                var renderable = _renderables[i];
                if (renderable.GetHorizontalDistance(pos) > distanceThreshold) {
                    continue;
                }

                if (!set) {
                    set = true;
                    _material.Set(h);
                }

                renderable.SetBuffers(h);
                _material.Draw(h, renderable.IndicesCount, SpecialRenderMode.Simple);
            }
        }

        public void Dispose() {
            DisposeHelper.Dispose(ref _material);
            _renderables.DisposeEverything();
        }

        [NotNull]
        public string Name => OriginalNode.Name ?? string.Empty;

        public int GetTrianglesCount() {
            return OriginalNode.TotalTrianglesCount;
        }

        int IRenderableObject.GetObjectsCount() => 1;
        Matrix IRenderableObject.ParentMatrix { get; set; }
        bool IRenderableObject.IsEnabled { get; set; }
        bool IRenderableObject.IsReflectable { get; set; }
        BoundingBox? IRenderableObject.BoundingBox => BoundingBox;
        public InputLayouts.VerticePNTG[] Vertices => _renderables[0].Vertices;

        void IRenderableObject.Draw(IDeviceContextHolder holder, ICamera camera, SpecialRenderMode mode, Func<IRenderableObject, bool> filter) { }
        void IRenderableObject.UpdateBoundingBox() { }
        IRenderableObject IRenderableObject.Clone() => throw new NotSupportedException();
        float? IRenderableObject.CheckIntersection(Ray ray) => null;
    }

    public class BakedObjectFilters {
        [CanBeNull]
        public IFilter<BakedObject> Tree;

        [CanBeNull]
        public IFilter<BakedObject> Grass;

        [CanBeNull]
        public IFilter<BakedObject> SyncNormals;

        [CanBeNull]
        public IFilter<BakedObject> RegularObjects;

        [CanBeNull]
        public IFilter<BakedObject> Surfaces;
    }

    public class Kn5RenderableObjectTester : ITester<BakedObject> {
        public static readonly Kn5RenderableObjectTester Instance = new Kn5RenderableObjectTester();

        public string ParameterFromKey(string key) {
            return null;
        }

        public bool Test(BakedObject obj, string key, ITestEntry value) {
            switch (key) {
                case null:
                    return value.Test(obj.Name);
                case "shader":
                    return value.Test(obj.GetShaderName());
                case "material":
                    return value.Test(obj.GetMaterialName());
                default:
                    return false;
            }
        }
    }

    public class MainBakery : UtilsRendererBase {
        private readonly string _filter;
        private readonly string _ignoreFilter;

        private readonly Kn5 _mainKn5;
        private readonly List<Kn5> _includeKn5 = new List<Kn5>();
        private readonly List<Kn5> _occludersKn5 = new List<Kn5>();

        private Kn5RenderableFile _mainNode;
        private List<Kn5RenderableFile> _includeNodeFiles = new List<Kn5RenderableFile>();
        private List<Kn5RenderableFile> _occluderNodeFiles = new List<Kn5RenderableFile>();
        // private List<Kn5RenderableFile> _occluderSplitNodeFiles = new List<Kn5RenderableFile>();

        private BakedObject[] _nodesToBake;
        private BakedObject[] _flattenNodes;
        private BakedObject[] _filteredNodes;
        private BakedObject[] _occluderNodes;

        private class BakingKn5Converter : IKn5ToRenderableConverter {
            private readonly Kn5 _kn5;
            private readonly BakedObjectFilters _filters;
            private readonly float _splitDistance;

            public BakingKn5Converter(Kn5 kn5, BakedObjectFilters filters, float splitDistance) {
                _kn5 = kn5;
                _filters = filters;
                _splitDistance = splitDistance;
            }

            public IRenderableObject Convert(Kn5Node node) {
                switch (node.NodeClass) {
                    case Kn5NodeClass.Base:
                        if (!node.Active) {
                            return new Kn5RenderableList(Kn5Node.CreateBaseNode("NULL"), this);
                        }

                        return new Kn5RenderableList(node, this);

                    case Kn5NodeClass.Mesh:
                    case Kn5NodeClass.SkinnedMesh:
                        if (!node.Active || !node.IsRenderable || !node.IsVisible || node.IsTransparent) {
                            return new Kn5RenderableList(Kn5Node.CreateBaseNode("NULL"), this);
                        }

                        return new BakedObject(node, _kn5, _filters, _splitDistance);

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        private Kn5RenderableFile LoadKn5Node(Kn5 kn5, FpsCamera camera, float splitDistance) {
            return new Kn5RenderableFile(kn5, Matrix.Identity, false,
                    new BakingKn5Converter(kn5, _filters, splitDistance));
        }

        private void InitializeNodes(FpsCamera camera) {
            Trace.WriteLine("Initializing nodes");

            _mainNode?.Dispose();
            _includeNodeFiles.DisposeEverything();
            GCHelper.CleanUp();

            _mainNode = LoadKn5Node(_mainKn5, camera, OccludersSplitThreshold);

            foreach (var kn5 in _includeKn5) {
                _includeNodeFiles.Add(LoadKn5Node(kn5, camera, OccludersSplitThreshold));
            }

            if (_occluderNodeFiles.Count == 0) {
                foreach (var kn5 in _occludersKn5) {
                    var node = LoadKn5Node(kn5, camera, OccludersSplitThreshold);
                    _occluderNodeFiles.Add(node);
                    // _occluderSplitNodeFiles.Add(node);
                }
            }

            Trace.WriteLine("Refreshing flatten nodes");
            RefreshFlatten();
            Trace.WriteLine("Refreshed flatten nodes");

            foreach (var n in _occluderNodes) {
                n.Initialize(DeviceContextHolder);
            }

            Trace.WriteLine("Initialized");
        }

        private void RefreshFlatten() {
            var filter = Filter.Create(Kn5RenderableObjectTester.Instance, _filter,
                    new FilterParams { StringMatchMode = StringMatchMode.CompleteMatch });
            var ignoreFilter = _ignoreFilter == null ? null
                    : Filter.Create(Kn5RenderableObjectTester.Instance, _ignoreFilter,
                            new FilterParams { StringMatchMode = StringMatchMode.CompleteMatch });
            var skipOccludersFilter = SkipOccludersFilter == null ? null
                    : Filter.Create(Kn5RenderableObjectTester.Instance, SkipOccludersFilter,
                            new FilterParams { StringMatchMode = StringMatchMode.CompleteMatch });

            bool IsMeshToBake(BakedObject n) {
                return filter.Test(n) && ignoreFilter?.Test(n) != true;
            }

            bool IsOccludingMesh(BakedObject n) {
                if (n.GetShaderName() == "ksGrass" || n.IsGrass) return false;
                if (n.GetMaterial()?.BlendMode != Kn5MaterialBlendMode.Opaque) return false;
                return skipOccludersFilter?.Test(n) != true;
            }

            _flattenNodes = new[] { _mainNode }.Concat(_includeNodeFiles).Concat(_occluderNodeFiles).SelectMany(FlattenFile).ToArray();
            _nodesToBake = new[] { _mainNode }.Concat(_includeNodeFiles).SelectMany(FlattenFile).Where(IsMeshToBake).ToArray();
            _occluderNodes = _flattenNodes.Where(IsOccludingMesh).ToArray();
            // _occluderNodes = _occluderSplitNodeFiles.SelectMany(file => FlattenFile(_filters, file).Where(IsOccludingMesh)).ToArray();
            // Trace.WriteLine("Occluding nodes: " + _occluderNodes.Length + "; names: " + _occluderNodes.Select(x => x.Name).JoinToString(", "));
            // Console.ReadLine();
        }

        private static IEnumerable<BakedObject> FlattenFile(Kn5RenderableFile file) {
            return file.SelectManyRecursive(x => x is Kn5RenderableList list && list.IsEnabled ? list : null).OfType<BakedObject>().Where(Filter);

            bool Filter(BakedObject o) {
                return o.OriginalNode.IsRenderable && !Regex.IsMatch(o.Name, @"^(?:AC_)", RegexOptions.IgnoreCase);
            }
        }

        protected override void DisposeOverride() {
            try {
                _mainNode.Dispose();
                _includeNodeFiles.DisposeEverything();
                _occluderNodeFiles.DisposeEverything();
                _state.Dispose();
                _color.Dispose();
                _depth.Dispose();
                _pool.GetList().DisposeEverything();
                base.DisposeOverride();
            } catch {
                // ignored
            }
        }

        protected override FeatureLevel FeatureLevel => FeatureLevel.Level_10_0;

        private class Kn5NodeLoader : IKn5NodeLoader {
            [NotNull]
            public static readonly Kn5NodeLoader Instance = new Kn5NodeLoader();

            private Kn5NodeLoader() { }
            public void OnNewKn5(string kn5Filename) { }

            private static Kn5Node LoadNode(Kn5Reader reader) {
                var node = reader.ReadNode();
                var capacity = node.Children.Capacity;

                try {
                    for (var i = 0; i < capacity; i++) {
                        var child = LoadNode(reader);
                        if (!child.Active || child.NodeClass != Kn5NodeClass.Base && !child.IsRenderable) {
                            continue;
                        }
                        node.Children.Add(child);
                    }
                } catch (EndOfStreamException) { }

                node.Children.Capacity = node.Children.Count;

                return node;
            }

            public Kn5Node LoadNode(ReadAheadBinaryReader reader) {
                return LoadNode((Kn5Reader)reader);
            }
        }

        private static readonly string[] ProtectedNames = {
            "txDiffuse",
            "txMask",
            "txDetailR",
            "txDetailG",
            "txDetailB",
            "txDetailA"
        };

        private static Kn5 LoadKn5(string filename, bool patchMode) {
            var result = Kn5.FromFile(filename, null, null, Kn5NodeLoader.Instance);
            if (patchMode) {
                var toRemove = result.TexturesData.Keys.Where(x => result.Materials.Values.All(
                        y => y.TextureMappings.Where(z => ProtectedNames.Contains(z.Name)).All(z => z.Texture != x))).ToList();
                if (toRemove.Any()) {
                    Trace.WriteLine("Unload unused textures: " + toRemove.JoinToString(", "));
                    foreach (var key in toRemove) {
                        result.TexturesData[key] = new byte[0];
                    }
                    GCHelper.CleanUp();
                }
            }
            return result;
        }

        public MainBakery(string mainKn5Filename, string filter, string ignoreFilter, bool createPatch)
                : this(LoadKn5(mainKn5Filename, createPatch)) {
            _createPatch = createPatch;
            _filter = filter;
            _ignoreFilter = ignoreFilter;
        }

        private MainBakery(Kn5 kn5) {
            _mainKn5 = kn5;
        }

        public MainBakery LoadExtraKn5(IEnumerable<string> includes, IEnumerable<string> occluders) {
            _includeKn5.Clear();
            _occludersKn5.Clear();

            foreach (var file in includes) {
                _includeKn5.Add(LoadKn5(file, _createPatch));
            }

            foreach (var file in occluders) {
                _occludersKn5.Add(LoadKn5(file, _createPatch));
            }

            return this;
        }

        protected override void ResizeInner() { }

        private BakeryMaterialsFactory _materialsFactory;
        private MultiKn5TexturesProvider _texturesProvider;

        protected override void InitializeInner() {
            _materialsFactory = new BakeryMaterialsFactory(_mainKn5);
            _texturesProvider = new MultiKn5TexturesProvider(new[] { _mainKn5 }, false);

            foreach (var kn5 in _includeKn5) {
                _texturesProvider.Kn5.Add(kn5);
            }

            foreach (var kn5 in _occludersKn5) {
                _texturesProvider.Kn5.Add(kn5);
            }

            DeviceContextHolder.Set<IMaterialsFactory>(_materialsFactory);
            DeviceContextHolder.Set<ITexturesProvider>(_texturesProvider);
        }

        private void Render(Vector3 pos, Vector3 look) {
            var h = DeviceContextHolder;
            for (var i = _filteredNodes.Length - 1; i >= 0; i--) {
                DeviceContext.Rasterizer.State = _state;
                _filteredNodes[i].RenderNode(pos, look, h, OccludersDistanceThreshold, _effect);
            }
        }

        private TargetResourceDepthTexture _depth;
        private TargetResourceTexture _color;
        private RasterizerState _state;

        private class ExtractTexture : IDisposable {
            public static Device CurrentDevice;
            public static Format CurrentFormat;
            public static int Width, Height;
            public readonly Texture2D Texture;
            public int VertexId;
            public BakeMode Mode;
            public BakedObject Prepared;
            public List<VertexGroup> Groups { get; set; }

            public ExtractTexture() {
                Texture = new Texture2D(CurrentDevice, new Texture2DDescription {
                    SampleDescription = new SampleDescription(1, 0),
                    Width = Width,
                    Height = Height,
                    ArraySize = 1,
                    MipLevels = 1,
                    Format = CurrentFormat,
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CpuAccessFlags = CpuAccessFlags.Read
                });
            }

            public void Dispose() {
                Texture?.Dispose();
            }
        }

        private readonly List<ExtractTexture> _extractQueue = new List<ExtractTexture>();
        private readonly Pool<ExtractTexture> _pool = new Pool<ExtractTexture>();

        protected override void DrawOverride() {
            PrepareForFinalPass();
            DeviceContextHolder.GetHelper<CopyHelper>().Draw(DeviceContextHolder, _color.View, RenderTargetView);
        }

        protected override void OnTickOverride(float dt) { }

        private long _progressBaked;
        private long _progressTotal;
        private Stopwatch _progressStopwatch;

        private class WeirdMesh {
            public Kn5Node Node;
            public Kn5 Kn5;
            public float MinValue, MaxValue;
        }

        public void Work(string saveTo) {
            Width = SampleResolution;
            Height = SampleResolution;

            if (!Initialized) {
                Initialize();
            }

            _filters = new BakedObjectFilters {
                Tree = TreeFilter == null ? null
                        : Filter.Create(Kn5RenderableObjectTester.Instance, TreeFilter,
                                new FilterParams { StringMatchMode = StringMatchMode.CompleteMatch }),
                Grass = GrassFilter == null ? null
                        : Filter.Create(Kn5RenderableObjectTester.Instance, GrassFilter,
                                new FilterParams { StringMatchMode = StringMatchMode.CompleteMatch }),
                RegularObjects = RegularObjectsFilter == null ? null
                        : Filter.Create(Kn5RenderableObjectTester.Instance, RegularObjectsFilter,
                                new FilterParams { StringMatchMode = StringMatchMode.CompleteMatch }),
                SyncNormals = SyncNormalsFilter == null ? null
                        : Filter.Create(Kn5RenderableObjectTester.Instance, SyncNormalsFilter,
                                new FilterParams { StringMatchMode = StringMatchMode.CompleteMatch }),
                Surfaces = SurfacesFilter == null ? null
                        : Filter.Create(Kn5RenderableObjectTester.Instance, SurfacesFilter,
                                new FilterParams { StringMatchMode = StringMatchMode.CompleteMatch }),
            };

            _effect = DeviceContextHolder.GetEffect<EffectBakeryShaders>();
            _depth = TargetResourceDepthTexture.Create();
            _depth.Resize(DeviceContextHolder, Width, Height, null);
            _color = TargetResourceTexture.Create(SampleFormat);
            _color.Resize(DeviceContextHolder, Width, Height, null);

            _state = RasterizerState.FromDescription(Device, new RasterizerStateDescription {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                IsAntialiasedLineEnabled = false,
                IsFrontCounterclockwise = false,
                IsDepthClipEnabled = true
            });

            var camera = new FpsCamera(CameraFov.ToRadians()) {
                NearZ = CameraNear,
                FarZ = CameraFar,
                DisableFrustum = true
            };
            camera.SetLens(1.0f);

            InitializeNodes(camera);

            _progressStopwatch = Stopwatch.StartNew();
            _progressTotal = _nodesToBake.Sum(x => x.OriginalNode.Vertices.Length) * (ExtraPass ? 2 : 1);
            _progressBaked = 0;

            /*if (SyncNormalsFilter != null) {
                _progressTotal += _nodesToBake.Count(x => !x.IsGrass && x.IsToSyncNormals);
            }

            if (SpecialGrassAmbient) {
                _progressTotal += _nodesToBake.Count(x => x.IsGrass);
            }*/

            Trace.WriteLine("Total meshes to bake: " + _nodesToBake.Length);
            Trace.WriteLine("Total vertices to bake: " + _progressTotal);

            var minLength = float.MaxValue;
            var maxLength = float.MinValue;
            var verticesChecked = 0L;
            var weirdMeshes = new List<WeirdMesh>();
            foreach (var file in new[] { _mainKn5 }.Concat(_includeKn5).Concat(_occludersKn5)) {
                EnumerateNodes(file.RootNode);

                void EnumerateNodes(Kn5Node node) {
                    if (node.NodeClass == Kn5NodeClass.Base) {
                        foreach (var child in node.Children) {
                            EnumerateNodes(child);
                        }
                    } else {
                        var weird = 0;
                        var nodeMinLength = float.MaxValue;
                        var nodeMaxLength = float.MinValue;
                        foreach (var vertex in node.Vertices) {
                            var length = vertex.TangentU.ToVector3().Length();
                            if (length < nodeMinLength) nodeMinLength = length;
                            else if (length > nodeMaxLength) nodeMaxLength = length;
                            if (length < 0.99f || length > 1.01f) weird++;
                            verticesChecked++;
                        }

                        if (nodeMinLength < minLength) minLength = nodeMinLength;
                        else if (nodeMaxLength > maxLength) maxLength = nodeMaxLength;

                        if (weird > 0) {
                            weirdMeshes.Add(new WeirdMesh {
                                Kn5 = file,
                                Node = node,
                                MinValue = nodeMinLength,
                                MaxValue = nodeMaxLength
                            });
                        }
                    }
                }
            }
            Trace.WriteLine($"Tangents lengths of {verticesChecked} vertices checked: min={minLength}, max={maxLength}");
            if (minLength < 0.99f || maxLength > 1.01f) {
                Trace.WriteLine("That’s a weird track you got there! Please, report it to the developer of this tool. List of strange meshes:");

                foreach (var mesh in weirdMeshes) {
                    Trace.WriteLine($"KN5: {Path.GetFileName(mesh.Kn5.OriginalFilename)}, mesh: {mesh.Node.Name}, "
                            + $"shader: {mesh.Kn5.GetMaterial(mesh.Node.MaterialId)?.ShaderName ?? "?"}, min.: {mesh.MinValue}, max.: {mesh.MaxValue}");
                }

                Trace.WriteLine("For now, press any key to continue.");
                Console.ReadLine();
            }

            ExtractTexture.CurrentDevice = Device;
            ExtractTexture.CurrentFormat = SampleFormat;
            ExtractTexture.Width = Width;
            ExtractTexture.Height = Height;

            DeviceContext.OutputMerger.SetTargets(_depth.DepthView, _color.TargetView);
            DeviceContext.Rasterizer.SetViewports(_color.Viewport);
            DeviceContext.OutputMerger.BlendState = null;
            DeviceContext.OutputMerger.DepthStencilState = null;

            _stopwatchSamples = new Stopwatch();
            _stopwatchSmoothing = new Stopwatch();

            foreach (var n in _nodesToBake) {
                BakeNode(n, camera, BakeMode.Normal);
            }
            FinalizeAll();

            if (ExtraPass) {
                Trace.WriteLine("Second pass");
                InitializeNodes(camera);

                Kn5MaterialToBake.SecondPass = true;
                Kn5MaterialToBake.SecondPassBrightnessGain = ExtraPassBrightnessGain;
                foreach (var n in _nodesToBake) {
                    BakeNode(n, camera, BakeMode.Normal);
                }
                FinalizeAll();
            }

            Trace.WriteLine($"Taken {_baked} samples: {_stopwatchSamples.Elapsed.ToReadableTime()} "
                    + $"({_baked / _stopwatchSamples.Elapsed.TotalMinutes / 1000:F0}k samples per minute)");
            Trace.WriteLine(
                    $"Smoothing: {_stopwatchSmoothing.Elapsed.TotalSeconds:F3} s (max. merged at once: {_mergedMaximum}, avg.: {(float)_mergedCount / _mergedVertices:F1})");

            /*if (SyncNormalsFilter != null) {
                Trace.WriteLine("Normals syncronization…");
                SyncNormalsGpu();
            }

            if (SpecialGrassAmbient) {
                Trace.WriteLine("Grass syncronization…");
                InitializeNodes(camera);
                SyncGrassGpu();
            }*/

            if (_createPatch) {
                using (var data = new MemoryStream())
                using (var writer = new ExtendedBinaryWriter(data)) {
                    foreach (var n in _nodesToBake) {
                        if (n.IsToSyncNormals) {
                            WriteNode(writer, n, true);
                        }
                        WriteNode(writer, n, false);
                    }

                    string checksum;
                    using (var stream = File.Open(_mainKn5.OriginalFilename, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var sha1 = SHA1.Create()) {
                        checksum = sha1.ComputeHash(stream).ToHexString().ToLowerInvariant();
                    }

                    var destination = saveTo ?? _mainKn5.OriginalFilename;
                    var patchFilename = Path.Combine(Path.GetDirectoryName(destination) ?? ".", Path.GetFileNameWithoutExtension(destination) + ".vao-patch");

                    Stream resultStream = null;
                    for (var i = 0; i < 10; i++) {
                        try {
                            resultStream = File.Create(i == 0 ? patchFilename : patchFilename + "_" + i);
                            break;
                        } catch (Exception) {
                            if (i == 9) throw;
                            Thread.Sleep(10);
                        }
                    }

                    if (resultStream == null) {
                        throw new NullReferenceException("That shouldn’t be happening");
                    }

                    using (var stream = resultStream)
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) {
                        zip.AddString("Manifest.json", JsonConvert.SerializeObject(new {
                            name = Path.GetFileName(saveTo ?? _mainKn5.OriginalFilename),
                            checksum
                        }));
                        zip.AddString("Config.ini", new IniFile() {
                            ["LIGHTING"] = {
                                ["OPACITY"] = ((double)AoOpacity).Round(0.0001),
                                ["BRIGHTNESS"] = ((double)AoMultiplier).Round(0.0001)
                            }
                        }.Stringify());
                        zip.AddBytes("Patch.data", data.ToArray());

                        var paramsFilename = Path.Combine(Path.GetDirectoryName(_mainKn5.OriginalFilename) ?? ".", "Baked Shadows Params.txt");
                        if (File.Exists(paramsFilename)) {
                            zip.AddBytes("Baked Shadows Params.txt", File.ReadAllBytes(paramsFilename));
                        }
                    }

                    Trace.WriteLine("Saved: " + patchFilename);
                }
            } else {
                _mainKn5.Save(saveTo ?? _mainKn5.OriginalFilename);
                Trace.WriteLine("Saved: " + (saveTo ?? _mainKn5.OriginalFilename));
            }
        }

        private void WriteNode(ExtendedBinaryWriter writer, BakedObject node, bool storeNormals) {
            if (storeNormals) {
                if (!WriteNode_Header(writer, node, PatchEntryType.Normal)) return;
                for (var i = 0; i < node.OriginalNode.Vertices.Length; i++) {
                    var vertex = node.OriginalNode.Vertices[i];
                    // Trace.WriteLine("Writing normal: " + node.Object.Name + ", ID: " + i + ", normal: " + vertex.Normal.ToVector3());
                    writer.WriteHalf(vertex.Normal[0]);
                    writer.WriteHalf(vertex.Normal[1]);
                    writer.WriteHalf(vertex.Normal[2]);
                }
                return;
            }

            var mult = node.IsSurface ? SurfacesAoOpacity : 1.0f;
            switch (node.Mode) {
                case BakedMode.TangentLength: {
                    if (!WriteNode_Header(writer, node, PatchEntryType.TangentLength)) return;
                    foreach (var vertex in node.OriginalNode.Vertices) {
                        writer.WriteHalf(mult.Lerp(1.0f, vertex.TangentU.ToVector3().Length() * 2.0f - 1.0f));
                    }
                    break;
                }
                case BakedMode.Tangent: {
                    if (!WriteNode_Header(writer, node, PatchEntryType.Tangent)) return;
                    foreach (var vertex in node.OriginalNode.Vertices) {
                        writer.WriteHalf(mult.Lerp(1.0f, vertex.TangentU[0] / 1e5f + 1.0f));
                        writer.WriteHalf(mult.Lerp(1.0f, vertex.TangentU[1] / 1e5f + 1.0f));
                        writer.WriteHalf(mult.Lerp(1.0f, vertex.TangentU[2] / 1e5f + 1.0f));

                        if (node.IsGrass) {
                            // Trace.WriteLine("Saved: " + vertex.TangentU[0]);
                        }
                    }
                    break;
                }
            }
        }

        private static bool WriteNode_Header(ExtendedBinaryWriter writer, BakedObject node, PatchEntryType mode) {
            var vertices = node.OriginalNode.Vertices;
            if (vertices.Length == 0 || node.Name.Length == 0) return false;

            writer.Write(node.Name);

            writer.Write((int)mode);
            writer.Write(vertices[0].Position[0]);
            writer.Write(vertices[0].Position[1]);
            writer.Write(vertices[0].Position[2]);

            writer.Write(vertices.Length);
            return true;
        }

        private BakedObjectFilters _filters;

        private void SyncGrassGpu() {
            var camera = new FpsCamera(1.0f.ToRadians()) {
                NearZ = CameraNear,
                FarZ = 12.0f,
                DisableFrustum = true
            };
            camera.SetLens(1.0f);

            Width = Height = 1;
            ExtractTexture.Width = Width;
            ExtractTexture.Height = Height;
            _color?.Dispose();
            _color = TargetResourceTexture.Create(Format.R32G32B32A32_UInt);
            ExtractTexture.CurrentFormat = Format.R32G32B32A32_UInt;
            _color.Resize(DeviceContextHolder, Width, Height, null);
            _depth.Resize(DeviceContextHolder, Width, Height, null);
            DeviceContext.OutputMerger.SetTargets(_depth.DepthView, _color.TargetView);
            DeviceContext.Rasterizer.SetViewports(_color.Viewport);
            _pool.GetList().DisposeEverything();
            _pool.Clear();

            var grass = _nodesToBake.Where(x => x.IsGrass).ToList();
            Trace.WriteLine($"Syncing grass with underlying surfaces: {grass.Count} {(grass.Count == 1 ? "mesh" : "meshes")}");

            var s = Stopwatch.StartNew();
            var surfaces = new[] { _mainNode }.Concat(_includeNodeFiles).Concat(_occluderNodeFiles).SelectMany(FlattenFile)
                                              .Where(x => !x.IsGrass && !x.IsTree && x.GetMaterial()?.AlphaTested == false).NonNull().ToArray();
            _occluderNodes = surfaces;

            // Trace.WriteLine(surfaces.Select(x => x.Baked.GetMaterialName()).Distinct().JoinToString("; "));
            Trace.WriteLine($"Surfaces preparation: {s.Elapsed.TotalMilliseconds:F1} ms");
            s.Restart();

            Kn5MaterialToBake.GrassPass = true;
            for (var grassIndex = 0; grassIndex < grass.Count; grassIndex++) {
                var o = grass[grassIndex];
                BakeNode(o, camera, BakeMode.SyncGrass);
            }
            FinalizeAll();

            Trace.WriteLine($"Grass meshes synced: {s.Elapsed.ToReadableTime()}");
        }

        private void SyncNormalsGpu() {
            var camera = new FpsCamera(1.0f.ToRadians()) {
                NearZ = CameraNear,
                FarZ = 100.0f,
                DisableFrustum = true
            };
            camera.SetLens(1.0f);

            Width = Height = 1;
            ExtractTexture.Width = Width;
            ExtractTexture.Height = Height;
            _color?.Dispose();
            _color = TargetResourceTexture.Create(Format.R32G32B32A32_UInt);
            ExtractTexture.CurrentFormat = Format.R32G32B32A32_UInt;
            _color.Resize(DeviceContextHolder, Width, Height, null);
            _depth.Resize(DeviceContextHolder, Width, Height, null);
            DeviceContext.OutputMerger.SetTargets(_depth.DepthView, _color.TargetView);
            DeviceContext.Rasterizer.SetViewports(_color.Viewport);
            _pool.GetList().DisposeEverything();
            _pool.Clear();

            var toSync = _nodesToBake.Where(x => !x.IsGrass && x.IsToSyncNormals).ToList();
            Trace.WriteLine($"Syncing normals with underlying surfaces: {toSync.Count} {(toSync.Count == 1 ? "mesh" : "meshes")}");

            var s = Stopwatch.StartNew();
            var surfaces = new[] { _mainNode }.Concat(_includeNodeFiles).Concat(_occluderNodeFiles).SelectMany(FlattenFile)
                                              .Where(x => !x.IsGrass && !x.IsToSyncNormals && !x.IsTree && x.GetMaterial()?.AlphaTested == false)
                                              .NonNull().ToArray();
            _occluderNodes = surfaces;

            // Trace.WriteLine(surfaces.Select(x => x.Baked.GetMaterialName()).Distinct().JoinToString("; "));
            Trace.WriteLine($"Occluders preparation: {s.Elapsed.TotalMilliseconds:F1} ms (found {_occluderNodes.Length} occluders)");
            s.Restart();

            Kn5MaterialToBake.GrassPass = true;
            for (var syncIndex = 0; syncIndex < toSync.Count; syncIndex++) {
                var o = toSync[syncIndex];
                BakeNode(o, camera, BakeMode.SyncNormals);
            }
            FinalizeAll();

            Trace.WriteLine($"Normals synced: {s.Elapsed.ToReadableTime()}");
        }

        private static void AddToVector(float[] destination, ref Vector3 value) {
            value.X += destination[0];
            value.Y += destination[1];
            value.Z += destination[2];
        }

        private static float Dot(float[] a, float[] b) {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        private static void SetVector(float[] destination, Vector3 value) {
            destination[0] = value.X;
            destination[1] = value.Y;
            destination[2] = value.Z;
        }

        private static void SetVector(float[] destination, float x, float y, float z) {
            destination[0] = x;
            destination[1] = y;
            destination[2] = z;
        }

        private readonly bool _createPatch;
        public bool ExtraPass;
        public bool SpecialGrassAmbient;
        public bool HdrSamples = false;
        private Format SampleFormat => HdrSamples ? Format.R32G32B32A32_Float : Format.R8G8B8A8_UNorm;

        public string TreeFilter;
        public string GrassFilter;
        public string RegularObjectsFilter;
        public string SurfacesFilter;
        public string SkipOccludersFilter;
        public string SyncNormalsFilter;

        public float AoMultiplier = 1.0f;
        public float AoOpacity = 0.92f;
        public float SurfacesAoOpacity = 0.6f;
        public float SaturationGain = 3.0f;
        public float SaturationInputMultiplier = 2.0f;
        public float CameraFov = 120.0f;
        public float CameraNear = 0.15f;
        public float CameraFar = 50.0f;
        public float CameraNormalOffsetUp = 0.2f;
        public float CameraOffsetAway = 0.16f;
        public float CameraOffsetUp = 0.08f;
        public float OccludersDistanceThreshold = 30.0f;
        public float ExtraPassBrightnessGain;
        public int MergeVertices = 30;
        public float MergeThreshold = 0.1f;
        public int QueueSize = 50;
        public int SampleResolution = 32;
        public float OccludersSplitThreshold = 0f;

        public bool DebugMode;
        public Vector3 DebugPoint;
        public float DebugRadius;

        private Stopwatch _stopwatchSamples, _stopwatchSmoothing;
        private long _baked;

        private class VertexGroup {
            public Vector3 Position;
            public Vector3 Normal;
            public readonly List<int> Indices = new List<int>();
        }

        private int _mergedMaximum;
        private int _mergedCount;
        private int _mergedVertices;
        private EffectBakeryShaders _effect;

        private enum BakeMode {
            Normal,
            SyncGrass,
            SyncNormals
        }

        private void BakeNode(BakedObject prepared, FpsCamera camera, BakeMode mode) {
            if (mode == BakeMode.Normal && SpecialGrassAmbient && prepared.IsGrass) {
                Trace.WriteLine("Skipping grass: " + prepared.Name);
                return;
            }

            var effect = _effect;
            if (DebugMode && prepared.GetHorizontalDistance(DebugPoint) > DebugRadius) {
                return;
            }

            _filteredNodes = _occluderNodes.Where(x => {
                var sameAsBaked = ReferenceEquals(x, prepared);
                if (sameAsBaked ? prepared.IsTree : x.OriginalNode.LodIn > 0.0f) {
                    return false;
                }

                return (x.BbCenter - prepared.BbCenter).Length() - x.BbSizeLength / 2 - prepared.BbSizeLength / 2 < OccludersDistanceThreshold;
            }).ToArray();

            _stopwatchSmoothing.Start();
            var size = prepared.OriginalNode.Vertices.Length;
            var groups = new List<VertexGroup>(size);
            var sorted = new List<int>(size);
            var threshold = MergeThreshold;

            for (var i = 0; i < size; i++) {
                sorted.Add(i);
            }

            /*sorted.Sort((left, right) => {
                var pa = prepared.OriginalNode.Vertices[left].Position[0];
                var pb = prepared.OriginalNode.Vertices[right].Position[0];
                if (float.IsNaN(pa)) return float.IsNaN(pb) ? 0 : 1;
                if (float.IsNaN(pb)) return -1;
                if (Math.Abs(pa - pb) < 0.001f) return 0;
                return pa > pb ? 1 : -1;
            });

            for (var iIndex = 0; iIndex < size; iIndex++) {
                var i = sorted[iIndex];
                if (i == -1) continue;

                var p = prepared.OriginalNode.Vertices[i].Position;
                var m = prepared.OriginalNode.Vertices[i].Normal;
                var g = new VertexGroup();

                g.Indices.Add(i);
                AddToVector(p, ref g.Position);
                AddToVector(m, ref g.Normal);

                for (int jIndex = iIndex + 1, x = Math.Min(iIndex + MergeVertices, size); jIndex < x; jIndex++) {
                    var j = sorted[jIndex];
                    if (j == -1) continue;

                    var jp = prepared.OriginalNode.Vertices[j].Position;
                    var jm = prepared.OriginalNode.Vertices[j].Normal;
                    if (jp[0] < p[0] - threshold || jp[0] > p[0] + threshold
                            || jp[1] < p[1] - threshold || jp[1] > p[1] + threshold
                            || jp[2] < p[2] - threshold || jp[2] > p[2] + threshold
                            || Dot(jm, m) < 0.5f) continue;

                    g.Indices.Add(j);
                    AddToVector(jp, ref g.Position);
                    AddToVector(jm, ref g.Normal);
                    sorted[jIndex] = -1;
                }

                g.Position /= g.Indices.Count;
                g.Normal /= g.Indices.Count;

                if (DebugMode) {
                    var distance = g.Position - DebugPoint;
                    distance.Y = 0f;
                    if (distance.Length() > DebugRadius) {
                        continue;
                    }
                }

                groups.Add(g);

                if (g.Indices.Count > _mergedMaximum) _mergedMaximum = g.Indices.Count;
                _mergedCount += g.Indices.Count;
                _mergedVertices++;
            }*/

            var obj = prepared;
            var length = size;

            sorted.Sort((left, right) => {
                var pa = obj.Vertices[left].Position.X;
                var pb = obj.Vertices[right].Position.X;
                if (float.IsNaN(pa)) return float.IsNaN(pb) ? 0 : 1;
                if (float.IsNaN(pb)) return -1;
                if (Math.Abs(pa - pb) < 0.001f) return 0;
                return pa > pb ? 1 : -1;
            });

            for (var iIndex = 0; iIndex < length; iIndex++) {
                var i = sorted[iIndex];
                if (i == -1) continue;

                var p = obj.Vertices[i].Position;
                var m = obj.Vertices[i].Normal;
                var g = new VertexGroup();

                g.Indices.Add(i);
                g.Position += p;
                g.Normal += m;

                for (int jIndex = iIndex + 1, x = Math.Min(iIndex + MergeVertices, length); jIndex < x; jIndex++) {
                    var j = sorted[jIndex];
                    if (j == -1) continue;

                    var jp = obj.Vertices[j].Position;
                    var jm = obj.Vertices[j].Normal;
                    if (jp.X < p.X - threshold || jp.X > p.X + threshold
                            || jp.Y < p.Y - threshold || jp.Y > p.Y + threshold
                            || jp.Z < p.Z - threshold || jp.Z > p.Z + threshold
                            || Vector3.Dot(m, jm) < 0.5f) continue;

                    g.Indices.Add(j);
                    g.Position += jp;
                    g.Normal += jm;
                    sorted[jIndex] = -1;
                }

                g.Position /= g.Indices.Count;
                g.Normal /= g.Indices.Count;
                groups.Add(g);

                if (g.Indices.Count > _mergedMaximum) _mergedMaximum = g.Indices.Count;
                _mergedCount += g.Indices.Count;
                _mergedVertices++;
            }

            _stopwatchSmoothing.Stop();

            if (groups.Count == 0) {
                _progressBaked += prepared.OriginalNode.TotalVerticesCount;
                return;
            }

            var s = Stopwatch.StartNew();
            _stopwatchSamples.Start();

            for (var i = 0; i < groups.Count; i += 1) {
                var group = groups[i];
                var world = group.Position;

                if (prepared.IsSurface) {
                    camera.Position = world + Vector3.UnitY * (CameraOffsetAway + CameraOffsetUp);
                    camera.Look = Vector3.UnitY;
                } else if (mode == BakeMode.Normal) {
                    camera.Position = prepared.IsTree
                            ? world - Vector3.UnitY * 0.5f
                            : world + group.Normal * CameraOffsetAway + Vector3.UnitY * CameraOffsetUp;
                    camera.Look = prepared.IsTree
                            ? Vector3.UnitY
                            : Vector3.Normalize(group.Normal + Vector3.UnitY * CameraNormalOffsetUp);
                } else {
                    camera.Position = world + Vector3.UnitY * (mode == BakeMode.SyncGrass ? 2.0f : 20.0f);
                    camera.Look = -Vector3.UnitY;
                }

                camera.Position = world + group.Normal * CameraOffsetAway + Vector3.UnitY * CameraOffsetUp;
                camera.Look = Vector3.Normalize(group.Normal + Vector3.UnitY * CameraNormalOffsetUp);

                camera.Right = Vector3.Normalize(Vector3.Cross(
                        Math.Abs(Vector3.Dot(camera.Look, Vector3.UnitY)) > 0.8f ? Vector3.UnitZ : Vector3.UnitY, camera.Look));
                camera.Up = Vector3.Normalize(Vector3.Cross(camera.Look, camera.Right));
                camera.UpdateViewMatrix();
                effect.FxWorldViewProj.SetMatrix(camera.ViewProj);

                // drawing:
                DeviceContext.ClearDepthStencilView(_depth.DepthView, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1f, 0);
                DeviceContext.ClearRenderTargetView(_color.TargetView, new Color4());
                DeviceContext.InputAssembler.InputLayout = effect.LayoutPNTG;

                Render(camera.Position, camera.Look);

                var copy = _pool.Get();
                copy.VertexId = i;
                copy.Mode = mode;
                copy.Groups = groups;
                copy.Prepared = prepared;
                Device.ImmediateContext.CopyResource(_color.Texture, copy.Texture);
                _extractQueue.Add(copy);
                ProcessQueue();

                _baked++;
            }

            _stopwatchSamples.Stop();
            _progressBaked += prepared.OriginalNode.TotalVerticesCount;

            var speed = _progressBaked / _progressStopwatch.Elapsed.TotalSeconds;
            var leftSeconds = TimeSpan.FromSeconds(1.2f * (_progressTotal - _progressBaked) / speed);

            Trace.WriteLine($"{prepared.Name}: taken {groups.Count} samples: {s.Elapsed.TotalMilliseconds:F1} ms "
                    + $"({groups.Count / s.Elapsed.TotalMinutes / 1000:F0}k samples per minute; "
                    + $"progress: {100d * _progressBaked / _progressTotal:F1}%; ETA: {leftSeconds.ToReadableTime()})");
        }

        void ProcessQueue() {
            var processAtOnce = Math.Min(100, QueueSize);
            if (_extractQueue.Count > QueueSize + processAtOnce) {
                for (var j = 0; j < processAtOnce; j++) {
                    Finalize(_extractQueue[j]);
                }
                _extractQueue.RemoveRange(0, processAtOnce);
            }
        }

        void FinalizeAll() {
            for (var j = 0; j < _extractQueue.Count; j++) {
                Finalize(_extractQueue[j]);
            }

            _extractQueue.Clear();
        }

        void Finalize(ExtractTexture t) {
            /*if (t.VertexId % 20 == 0) {
                var yVal = obj.Vertices[groups[t.VertexId].Indices[0]].Position.Y;
                Resource.SaveTextureToFile(Device.ImmediateContext, t.Texture, ImageFileFormat.Dds,
                        $@"D:\Temporary\Big Piles of Crap\{t.VertexId}_{yVal:F4}.dds");
            }*/

            var rect = Device.ImmediateContext.MapSubresource(t.Texture, 0, MapMode.Read, SlimDX.Direct3D11.MapFlags.None);

            var satGain = SaturationGain;
            var satInputMult = SaturationInputMultiplier;
            var satInputMultInv = 0.5f / satInputMult;
            var avgAoOnly = ExtraPass && !Kn5MaterialToBake.SecondPass;

            try {
                var a = Vector3.Zero;
                var n = Vector3.Zero;

                using (var bb = new ReadAheadBinaryReader(rect.Data)) {
                    for (var y = 0; y < Height; y++) {
                        bb.Seek(rect.RowPitch * y, SeekOrigin.Begin);
                        for (var x = 0; x < Width; x++) {
                            float r, g, b, w;
                            switch (t.Mode) {
                                case BakeMode.SyncGrass: {
                                    bb.ReadUInt32_4D(out var vx, out var vy, out var vz, out var vw);
                                    r = Half.ToHalf((ushort)(vx >> 16));
                                    g = Half.ToHalf((ushort)(vx & 0xffff));
                                    b = Half.ToHalf((ushort)(vy >> 16));
                                    w = Half.ToHalf((ushort)(vy & 0xffff));
                                    n.X += Half.ToHalf((ushort)(vz >> 16));
                                    n.Y += Half.ToHalf((ushort)(vz & 0xffff));
                                    n.Z += Half.ToHalf((ushort)(vw >> 16));

                                    var nw = 1.0f - w;
                                    a.X = r * w + nw;
                                    a.Y = g * w + nw;
                                    a.Z = b * w + nw;
                                    break;
                                }
                                case BakeMode.SyncNormals: {
                                    bb.ReadUInt32_4D(out _, out _, out var vz, out var vw);
                                    n.X += Half.ToHalf((ushort)(vz >> 16));
                                    n.Y += Half.ToHalf((ushort)(vz & 0xffff));
                                    n.Z += Half.ToHalf((ushort)(vw >> 16));
                                    break;
                                }
                                default:
                                    if (HdrSamples) {
                                        bb.ReadSingle4D(out r, out g, out b, out w);
                                    } else {
                                        bb.ReadByte4D(out var byteR, out var byteG, out var byteB, out var byteW);
                                        r = byteR / 255.0f;
                                        g = byteG / 255.0f;
                                        b = byteB / 255.0f;
                                        w = byteW / 255.0f;
                                    }

                                    if (avgAoOnly) {
                                        var nw = 1.0f - w;
                                        var av = w * (r + g + b) / 3.0f + nw;
                                        a.X += av;
                                        a.Y += av;
                                        a.Z += av;
                                    } else {
                                        var sw = w * (1.0f + satGain * (GetSaturation(r, g, b) * satInputMult - satInputMultInv).Saturate());
                                        var nw = 1.0f - w;
                                        a.X += sw * r + nw;
                                        a.Y += sw * g + nw;
                                        a.Z += sw * b + nw;
                                    }
                                    break;
                            }
                        }
                    }
                }

                a /= Width * Height;

                var groups = t.Groups;
                var obj = t.Prepared;
                var ind = groups[t.VertexId].Indices;

                if (t.Mode == BakeMode.SyncGrass) {
                    if (t.Prepared.IsToSyncNormals) {
                        n /= Width * Height;
                        if (n.Length() > 0.001f) {
                            n.Normalize();
                            for (var i = ind.Count - 1; i >= 0; i--) {
                                SetVector(obj.OriginalNode.Vertices[groups[t.VertexId].Indices[i]].Normal, n);
                            }
                        }
                    }
                } else if (t.Mode == BakeMode.SyncNormals) {
                    n /= Width * Height;
                    if (n.Length() > 0.001f) {
                        n.Normalize();
                        for (var i = ind.Count - 1; i >= 0; i--) {
                            // Trace.WriteLine("Storing normal: " + obj.Name + ", ID: " + groups[t.VertexId].Indices[i] + ", normal: " + n);
                            SetVector(obj.OriginalNode.Vertices[groups[t.VertexId].Indices[i]].Normal, n);
                        }
                    }
                } else if (_createPatch && (!ExtraPass || Kn5MaterialToBake.SecondPass)) {
                    a.X = a.X.Saturate();
                    a.Y = a.Y.Saturate();
                    a.Z = a.Z.Saturate();
                } else {
                    var aoOpacity = t.Prepared.IsSurface ? AoOpacity * SurfacesAoOpacity : AoOpacity;
                    a = new Vector3(1.0f - aoOpacity) + a * aoOpacity;
                    a.X = a.X.Saturate();
                    a.Y = a.Y.Saturate();
                    a.Z = a.Z.Saturate();
                    a *= AoMultiplier;
                }

                if (t.Mode != BakeMode.SyncNormals) {
                    switch (t.Prepared.Mode) {
                        case BakedMode.TangentLength:
                            for (var i = ind.Count - 1; i >= 0; i--) {
                                var avg = (a.X + a.Y + a.Z) / 3.0f;
                                var g = ind[i];
                                var v = Vector3.Normalize(obj.OriginalNode.Vertices[g].TangentU.ToVector3()) * (0.5f + avg * 0.5f);
                                SetVector(obj.OriginalNode.Vertices[g].TangentU, v);
                            }
                            break;
                        default:
                            for (var i = ind.Count - 1; i >= 0; i--) {
                                var g = ind[i];
                                SetVector(obj.OriginalNode.Vertices[g].TangentU,
                                        a.X * 1e5f - 1e5f, a.Y * 1e5f - 1e5f, a.Z * 1e5f - 1e5f);
                            }
                            break;
                    }
                }
            } finally {
                Device.ImmediateContext.UnmapSubresource(t.Texture, 0);
            }
            _pool.Add(t);
        }

        private static float GetSaturation(float r, float g, float b) {
            if (r == g && g == b) return 0;

            var max = r;
            var min = r;

            if (g > max) max = g;
            else if (g < min) min = g;

            if (b > max) max = b;
            else if (b < min) min = b;

            var sum = max + min;
            return (max - min) / (sum > 1.0f ? 2.0f - sum : sum + 0.00001f);
        }
    }
}