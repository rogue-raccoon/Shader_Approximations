using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Thesis.Glass.EditorTools
{
    public static class GlassProxyMeshGenerator
    {
        const string OutputDir = "Assets/VRShaders/Glass/Meshes";

        public const float SphereRadius     = 0.5f;
        public const float PrismInradius    = 0.4f;
        public const float PrismHalfHeight  = 0.5f;
        public const float LensCurvature    = 0.9f;  
        public const float LensHalfThick    = 0.15f;

        [MenuItem("Tools/Generate Proxy Meshes")]
        public static void Generate()
        {
            Directory.CreateDirectory(OutputDir);

            Save(BuildSphere(SphereRadius, 48, 24), "SM_Proxy_Sphere");
            Save(BuildPrism(PrismInradius, PrismHalfHeight), "SM_Proxy_Prism");
            Save(BuildLens(LensCurvature, LensHalfThick, 64, 16), "SM_Proxy_Lens");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            float rim = Mathf.Sqrt(LensCurvature * LensCurvature -
                                   Mathf.Pow(LensCurvature - LensHalfThick, 2f));
            Debug.Log(
                "[ProxyMesh] Written to " + OutputDir + "\n" +
                $"  Sphere _ProxyExtents = ({SphereRadius}, 0, 0, 0)\n" +
                $"  Prism  _ProxyExtents = ({PrismInradius}, {PrismHalfHeight}, 0, 0)\n" +
                $"  Lens   _ProxyExtents = ({LensCurvature}, {LensHalfThick}, 0, 0)  rim radius {rim:F3}");
        }

        static void Save(Mesh m, string name)
        {
            string path = $"{OutputDir}/{name}.asset";
            m.name = name;
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(m, existing);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(m);
            }
            else AssetDatabase.CreateAsset(m, path);
        }

        static Mesh BuildSphere(float radius, int segments, int rings)
        {
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();

            for (int y = 0; y <= rings; y++)
            {
                float vAng = Mathf.PI * y / rings;
                for (int x = 0; x <= segments; x++)
                {
                    float uAng = 2f * Mathf.PI * x / segments;
                    var dir = new Vector3(Mathf.Sin(vAng) * Mathf.Cos(uAng),
                                          Mathf.Cos(vAng),
                                          Mathf.Sin(vAng) * Mathf.Sin(uAng));
                    v.Add(dir * radius);
                    n.Add(dir);
                    uv.Add(new Vector2((float)x / segments, 1f - (float)y / rings));
                }
            }

            int stride = segments + 1;
            for (int y = 0; y < rings; y++)
                for (int x = 0; x < segments; x++)
                {
                    int a = y * stride + x, b = a + 1, c = a + stride, d = c + 1;
                    if (y > 0) { tri.Add(a); tri.Add(b); tri.Add(c); }
                    if (y < rings - 1) { tri.Add(b); tri.Add(d); tri.Add(c); }
                }

            return Assemble(v, n, uv, tri);
        }

        static Mesh BuildPrism(float inradius, float halfHeight)
        {
            float circum = 2f * inradius;
            var corner = new Vector2[3];
            for (int i = 0; i < 3; i++)
            {
                float a = Mathf.Deg2Rad * (90f + 120f * i + 60f);
                corner[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * circum;
            }

            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();

            for (int i = 0; i < 3; i++)
            {
                Vector2 p0 = corner[i], p1 = corner[(i + 1) % 3];
                Vector3 fn = new Vector3(p0.x + p1.x, 0f, p0.y + p1.y).normalized;

                int b = v.Count;
                v.Add(new Vector3(p0.x, -halfHeight, p0.y));
                v.Add(new Vector3(p1.x, -halfHeight, p1.y));
                v.Add(new Vector3(p0.x,  halfHeight, p0.y));
                v.Add(new Vector3(p1.x,  halfHeight, p1.y));
                for (int k = 0; k < 4; k++) n.Add(fn);
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0));
                uv.Add(new Vector2(0, 1)); uv.Add(new Vector2(1, 1));

                tri.Add(b); tri.Add(b + 2); tri.Add(b + 1);
                tri.Add(b + 1); tri.Add(b + 2); tri.Add(b + 3);
            }

            for (int s = 0; s < 2; s++)
            {
                float y = s == 0 ? halfHeight : -halfHeight;
                Vector3 fn = s == 0 ? Vector3.up : Vector3.down;
                int b = v.Count;
                for (int i = 0; i < 3; i++)
                {
                    v.Add(new Vector3(corner[i].x, y, corner[i].y));
                    n.Add(fn);
                    uv.Add(new Vector2(corner[i].x, corner[i].y) * 0.5f + Vector2.one * 0.5f);
                }
                if (s == 0) { tri.Add(b); tri.Add(b + 2); tri.Add(b + 1); }
                else        { tri.Add(b); tri.Add(b + 1); tri.Add(b + 2); }
            }

            return Assemble(v, n, uv, tri);
        }

        static Mesh BuildLens(float R, float halfThick, int segments, int rings)
        {
            float offset = R - halfThick;
            float rim = Mathf.Sqrt(Mathf.Max(R * R - offset * offset, 1e-6f));

            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();

            for (int s = 0; s < 2; s++)
            {
                float sign = s == 0 ? 1f : -1f;
                Vector3 c = new Vector3(0, 0, -sign * offset);
                int baseIdx = v.Count;

                for (int j = 0; j <= rings; j++)
                {
                    float r = rim * j / rings;
                    float z = sign * (Mathf.Sqrt(Mathf.Max(R * R - r * r, 0f)) - offset);
                    for (int i = 0; i <= segments; i++)
                    {
                        float a = 2f * Mathf.PI * i / segments;
                        var p = new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
                        v.Add(p);
                        n.Add((p - c).normalized);
                        uv.Add(new Vector2((float)i / segments, (float)j / rings));
                    }
                }

                int stride = segments + 1;
                for (int j = 0; j < rings; j++)
                    for (int i = 0; i < segments; i++)
                    {
                        int a = baseIdx + j * stride + i, b = a + 1;
                        int cc = a + stride, d = cc + 1;
                        if (s == 0)
                        {
                            if (j > 0) { tri.Add(a); tri.Add(cc); tri.Add(b); }
                            tri.Add(b); tri.Add(cc); tri.Add(d);
                        }
                        else
                        {
                            if (j > 0) { tri.Add(a); tri.Add(b); tri.Add(cc); }
                            tri.Add(b); tri.Add(d); tri.Add(cc);
                        }
                    }
            }

            return Assemble(v, n, uv, tri);
        }

        static Mesh Assemble(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> tri)
        {
            var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetUVs(0, uv);
            m.SetTriangles(tri, 0);
            m.RecalculateTangents();
            m.RecalculateBounds();
            return m;
        }
    }
}
