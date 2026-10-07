using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GrblHost.Core.GCode;

namespace GrblHost.Controls;

/// <summary>
/// Builds 3D meshes of toolpath segments. A move is a "roof" with a
/// triangular cross section (two slopes, 6 vertices): with smoothed normals
/// it looks like a round tube and stays cheap for big files. The color comes
/// from the U texture coordinate (see <see cref="FeaturePalette"/>).
/// </summary>
internal static class ToolpathMesh
{
    public const double RapidWidth = 0.15;
    public const double CutWidth = 0.4;

    /// <summary>Mesh of segments [first, end), feed moves or rapids. Frozen, null if empty.</summary>
    public static MeshGeometry3D? Build(Toolpath tp, int first, int end, bool rapid)
    {
        var segs = tp.Segments;
        end = Math.Min(end, segs.Length);
        int count = 0;
        for (int i = first; i < end; i++)
            if ((segs[i].Kind == MoveKind.Rapid) == rapid)
                count++;
        if (count == 0)
            return null;

        var positions = new Point3DCollection(count * 6);
        var indices = new Int32Collection(count * 12);
        var tex = new PointCollection(count * 6);

        for (int i = first; i < end; i++)
        {
            ref readonly var s = ref segs[i];
            if ((s.Kind == MoveKind.Rapid) != rapid)
                continue;
            double dx = s.End.X - s.Start.X, dy = s.End.Y - s.Start.Y;
            double len2 = Math.Sqrt(dx * dx + dy * dy);

            double width = rapid ? RapidWidth : CutWidth;
            double height = width;

            // Side vector, perpendicular to the move in XY. Pure Z moves get any side.
            double nx, ny;
            if (len2 > 1e-9)
            {
                nx = -dy / len2 * width / 2;
                ny = dx / len2 * width / 2;
            }
            else
            {
                nx = width / 2;
                ny = 0;
            }

            // The path runs through the middle of the cross section.
            double top0 = s.Start.Z + height / 2, top1 = s.End.Z + height / 2;
            double bot0 = top0 - height, bot1 = top1 - height;
            int b = positions.Count;
            // 0: left start, 1: top start, 2: right start, 3..5 the same at the end.
            positions.Add(new Point3D(s.Start.X + nx, s.Start.Y + ny, bot0));
            positions.Add(new Point3D(s.Start.X, s.Start.Y, top0));
            positions.Add(new Point3D(s.Start.X - nx, s.Start.Y - ny, bot0));
            positions.Add(new Point3D(s.End.X + nx, s.End.Y + ny, bot1));
            positions.Add(new Point3D(s.End.X, s.End.Y, top1));
            positions.Add(new Point3D(s.End.X - nx, s.End.Y - ny, bot1));

            // Left slope (outward normal +side +Z), right slope (-side +Z).
            indices.Add(b + 0); indices.Add(b + 1); indices.Add(b + 4);
            indices.Add(b + 0); indices.Add(b + 4); indices.Add(b + 3);
            indices.Add(b + 2); indices.Add(b + 4); indices.Add(b + 1);
            indices.Add(b + 2); indices.Add(b + 5); indices.Add(b + 4);

            var uv = new Point(FeaturePalette.TextureU(s.Feature), 0.5);
            for (int k = 0; k < 6; k++)
                tex.Add(uv);
        }

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            TriangleIndices = indices,
            TextureCoordinates = tex,
        };
        mesh.Freeze();
        return mesh;
    }

    /// <summary>A flat quad from (x0,y0) to (x1,y1) at height z, width w.</summary>
    public static void AddLine(MeshGeometry3D mesh, double x0, double y0, double x1, double y1, double z, double w)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9)
            return;
        double nx = -dy / len * w / 2, ny = dx / len * w / 2;
        int b = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(x0 + nx, y0 + ny, z));
        mesh.Positions.Add(new Point3D(x0 - nx, y0 - ny, z));
        mesh.Positions.Add(new Point3D(x1 - nx, y1 - ny, z));
        mesh.Positions.Add(new Point3D(x1 + nx, y1 + ny, z));
        mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(b + 2);
        mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 2); mesh.TriangleIndices.Add(b + 3);
    }

    /// <summary>A box between two corners.</summary>
    public static void AddBox(MeshGeometry3D mesh, Point3D a, Point3D c)
    {
        int b = mesh.Positions.Count;
        for (int i = 0; i < 8; i++)
            mesh.Positions.Add(new Point3D((i & 1) == 0 ? a.X : c.X, (i & 2) == 0 ? a.Y : c.Y, (i & 4) == 0 ? a.Z : c.Z));
        int[] faces =
        {
            0, 2, 3, 0, 3, 1,   // bottom
            4, 5, 7, 4, 7, 6,   // top
            0, 1, 5, 0, 5, 4,   // front
            2, 6, 7, 2, 7, 3,   // back
            0, 4, 6, 0, 6, 2,   // left
            1, 3, 7, 1, 7, 5,   // right
        };
        foreach (int f in faces)
            mesh.TriangleIndices.Add(b + f);
    }

    /// <summary>
    /// Tool marker: an end mill (diameter 3.175 mm) with the tip at the origin,
    /// a wider collet nut above it.
    /// </summary>
    public static MeshGeometry3D CreateTool()
    {
        var mesh = new MeshGeometry3D();
        AddCylinder(mesh, 1.6, 0, 18, 20);
        AddCylinder(mesh, 7, 18, 30, 6);
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Closed cylinder around the Z axis from z0 to z1.</summary>
    private static void AddCylinder(MeshGeometry3D mesh, double r, double z0, double z1, int n)
    {
        int b = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(0, 0, z0));
        mesh.Positions.Add(new Point3D(0, 0, z1));
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            mesh.Positions.Add(new Point3D(r * Math.Cos(a), r * Math.Sin(a), z0));
            mesh.Positions.Add(new Point3D(r * Math.Cos(a), r * Math.Sin(a), z1));
        }
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            int a0 = b + 2 + i * 2, a1 = a0 + 1, c0 = b + 2 + j * 2, c1 = c0 + 1;
            mesh.TriangleIndices.Add(a0); mesh.TriangleIndices.Add(c0); mesh.TriangleIndices.Add(c1);
            mesh.TriangleIndices.Add(a0); mesh.TriangleIndices.Add(c1); mesh.TriangleIndices.Add(a1);
            mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c0); mesh.TriangleIndices.Add(a0);
            mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(a1); mesh.TriangleIndices.Add(c1);
        }
    }

    public static Material Solid(Color color, double specular = 0)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Material m = new DiffuseMaterial(brush);
        if (specular > 0)
        {
            var g = new MaterialGroup();
            g.Children.Add(m);
            g.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), specular));
            m = g;
        }
        m.Freeze();
        return m;
    }
}
